using System.Globalization;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

public static class WikiDatabaseComputation
{
    public static void ValidateFormula(string expression, IReadOnlyCollection<string>? availablePropertyNames = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new ArgumentException("A formula expression is required.", nameof(expression));
        }

        _ = new FormulaParser(expression, propertyName =>
        {
            if (availablePropertyNames is not null
                && !availablePropertyNames.Contains(propertyName, StringComparer.OrdinalIgnoreCase))
            {
                throw new FormulaEvaluationException($"#REF! Unknown property [{propertyName}]");
            }
            return WikiComputedValue.Number(0);
        }, validationOnly: true).Parse();
    }

    public static void Materialize(
        WikiDatabase database,
        IReadOnlyDictionary<Guid, WikiDatabase> relatedDatabases)
    {
        var databases = new Dictionary<Guid, WikiDatabase>(relatedDatabases)
        {
            [database.Id] = database
        };

        foreach (var relationProperty in database.Properties.Where(property => property.Type == WikiDatabasePropertyTypes.Relation))
        {
            var config = WikiDatabasePropertyConfig.Parse(relationProperty);
            if (config.RelatedDatabaseId is not { } relatedDatabaseId
                || !databases.TryGetValue(relatedDatabaseId, out var relatedDatabase))
            {
                continue;
            }

            var titleProperty = relatedDatabase.Properties.FirstOrDefault(property => property.Type == WikiDatabasePropertyTypes.Title);
            var options = relatedDatabase.Rows.OrderBy(row => row.SortOrder)
                .Select(row => new WikiDatabasePropertyOption(
                    row.Id.ToString(),
                    titleProperty is null
                        ? "Untitled"
                        : WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), titleProperty.Id) ?? "Untitled",
                    "#78716c"))
                .ToList();
            relationProperty.ConfigJson = WikiDatabasePropertyConfig.Serialize(config with { Options = options });
        }

        foreach (var row in database.Rows)
        {
            var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
            foreach (var property in database.Properties.Where(property =>
                         property.Type is WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup))
            {
                var computed = EvaluateProperty(database, row, property, databases, []);
                values[property.Id.ToString()] = computed.ToJsonNode();
            }
            row.PropertyValuesJson = WikiPropertyValues.Serialize(values);
        }
    }

    private static WikiComputedValue EvaluateProperty(
        WikiDatabase database,
        WikiDatabaseRow row,
        WikiDatabaseProperty property,
        IReadOnlyDictionary<Guid, WikiDatabase> databases,
        HashSet<(Guid RowId, Guid PropertyId)> evaluationPath)
    {
        var key = (row.Id, property.Id);
        if (!evaluationPath.Add(key))
        {
            return WikiComputedValue.Text("#CYCLE!");
        }

        try
        {
            return property.Type switch
            {
                WikiDatabasePropertyTypes.Formula => EvaluateFormula(database, row, property, databases, evaluationPath),
                WikiDatabasePropertyTypes.Rollup => EvaluateRollup(database, row, property, databases, evaluationPath),
                _ => ReadStoredValue(database, row, property, databases, evaluationPath)
            };
        }
        catch (FormulaEvaluationException exception)
        {
            return WikiComputedValue.Text(exception.Message);
        }
        finally
        {
            evaluationPath.Remove(key);
        }
    }

    private static WikiComputedValue EvaluateFormula(
        WikiDatabase database,
        WikiDatabaseRow row,
        WikiDatabaseProperty property,
        IReadOnlyDictionary<Guid, WikiDatabase> databases,
        HashSet<(Guid RowId, Guid PropertyId)> evaluationPath)
    {
        var expression = WikiDatabasePropertyConfig.Parse(property).FormulaExpression;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return WikiComputedValue.Empty;
        }

        return new FormulaParser(expression, propertyName =>
        {
            var referenced = database.Properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase));
            if (referenced is null)
            {
                throw new FormulaEvaluationException($"#REF! Unknown property [{propertyName}]");
            }

            return EvaluateProperty(database, row, referenced, databases, evaluationPath);
        }).Parse();
    }

    private static WikiComputedValue EvaluateRollup(
        WikiDatabase database,
        WikiDatabaseRow row,
        WikiDatabaseProperty property,
        IReadOnlyDictionary<Guid, WikiDatabase> databases,
        HashSet<(Guid RowId, Guid PropertyId)> evaluationPath)
    {
        var config = WikiDatabasePropertyConfig.Parse(property);
        if (config.RelationPropertyId is not { } relationPropertyId
            || config.RollupPropertyId is not { } rollupPropertyId)
        {
            return WikiComputedValue.Empty;
        }

        var relationProperty = database.Properties.FirstOrDefault(candidate =>
            candidate.Id == relationPropertyId && candidate.Type == WikiDatabasePropertyTypes.Relation);
        if (relationProperty is null)
        {
            return WikiComputedValue.Text("#REF! Relation property missing");
        }

        var relationConfig = WikiDatabasePropertyConfig.Parse(relationProperty);
        if (relationConfig.RelatedDatabaseId is not { } relatedDatabaseId
            || !databases.TryGetValue(relatedDatabaseId, out var relatedDatabase))
        {
            return WikiComputedValue.Text("#REF! Related database missing");
        }

        var targetProperty = relatedDatabase.Properties.FirstOrDefault(candidate => candidate.Id == rollupPropertyId);
        if (targetProperty is null)
        {
            return WikiComputedValue.Text("#REF! Rollup property missing");
        }

        var rowIds = WikiPropertyValues.GetMultiSelect(WikiPropertyValues.ParseObject(row.PropertyValuesJson), relationProperty.Id)
            .Select(value => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty)
            .Where(value => value != Guid.Empty)
            .ToHashSet();
        var relatedRows = relatedDatabase.Rows.Where(candidate => rowIds.Contains(candidate.Id)).ToList();
        var values = relatedRows
            .Select(candidate => EvaluateProperty(relatedDatabase, candidate, targetProperty, databases, evaluationPath))
            .Where(value => !value.IsEmpty)
            .ToList();

        return (config.RollupAggregation ?? WikiDatabaseRollupAggregations.Count) switch
        {
            WikiDatabaseRollupAggregations.Count => WikiComputedValue.Number(relatedRows.Count),
            WikiDatabaseRollupAggregations.CountValues => WikiComputedValue.Number(values.Count),
            WikiDatabaseRollupAggregations.Sum => AggregateNumbers(values, numbers => numbers.Sum()),
            WikiDatabaseRollupAggregations.Average => AggregateNumbers(values, numbers => numbers.Count == 0 ? 0 : numbers.Average()),
            WikiDatabaseRollupAggregations.Minimum => AggregateNumbers(values, numbers => numbers.Count == 0 ? 0 : numbers.Min()),
            WikiDatabaseRollupAggregations.Maximum => AggregateNumbers(values, numbers => numbers.Count == 0 ? 0 : numbers.Max()),
            WikiDatabaseRollupAggregations.ShowUnique => WikiComputedValue.Text(string.Join(", ", values
                .Select(value => value.ToDisplayText())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase))),
            _ => WikiComputedValue.Text("#ERROR! Unsupported rollup")
        };
    }

    private static WikiComputedValue AggregateNumbers(
        IReadOnlyList<WikiComputedValue> values,
        Func<IReadOnlyList<decimal>, decimal> aggregate)
    {
        var numbers = values.Select(value => value.TryAsNumber(out var number) ? new decimal?(number) : null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        return WikiComputedValue.Number(aggregate(numbers));
    }

    private static WikiComputedValue ReadStoredValue(
        WikiDatabase database,
        WikiDatabaseRow row,
        WikiDatabaseProperty property,
        IReadOnlyDictionary<Guid, WikiDatabase> databases,
        HashSet<(Guid RowId, Guid PropertyId)> evaluationPath)
    {
        if (property.Type is WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup)
        {
            return EvaluateProperty(database, row, property, databases, evaluationPath);
        }

        var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
        return property.Type switch
        {
            WikiDatabasePropertyTypes.Number => WikiPropertyValues.GetNumber(values, property.Id) is { } number
                ? WikiComputedValue.Number(number) : WikiComputedValue.Empty,
            WikiDatabasePropertyTypes.Checkbox => WikiComputedValue.Boolean(WikiPropertyValues.GetCheckbox(values, property.Id)),
            WikiDatabasePropertyTypes.Date => WikiPropertyValues.GetDate(values, property.Id) is { } date
                ? WikiComputedValue.Date(date) : WikiComputedValue.Empty,
            WikiDatabasePropertyTypes.CreatedTime => WikiComputedValue.Date(row.CreatedAt),
            WikiDatabasePropertyTypes.MultiSelect or WikiDatabasePropertyTypes.Person or WikiDatabasePropertyTypes.Files or WikiDatabasePropertyTypes.Relation =>
                WikiComputedValue.Text(string.Join(", ", WikiPropertyValues.GetMultiSelect(values, property.Id))),
            _ => WikiComputedValue.Text(WikiPropertyValues.GetDisplayText(property, values, row.CreatedAt))
        };
    }

    private readonly record struct WikiComputedValue(object? Value)
    {
        public static WikiComputedValue Empty => new(null);
        public static WikiComputedValue Number(decimal value) => new(value);
        public static WikiComputedValue Boolean(bool value) => new(value);
        public static WikiComputedValue Text(string? value) => new(value);
        public static WikiComputedValue Date(DateTimeOffset value) => new(value);
        public bool IsEmpty => Value is null || Value is string text && string.IsNullOrWhiteSpace(text);

        public bool TryAsNumber(out decimal number)
        {
            if (Value is decimal decimalValue)
            {
                number = decimalValue;
                return true;
            }
            return decimal.TryParse(Value?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number);
        }

        public bool AsBoolean() => Value switch
        {
            bool boolean => boolean,
            decimal number => number != 0,
            DateTimeOffset => true,
            string text => !string.IsNullOrWhiteSpace(text) && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        public bool TryAsDate(out DateTimeOffset date)
        {
            if (Value is DateTimeOffset dateValue)
            {
                date = dateValue;
                return true;
            }
            return DateTimeOffset.TryParse(Value?.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out date);
        }

        public string ToDisplayText() => Value switch
        {
            null => string.Empty,
            bool boolean => boolean ? "True" : "False",
            decimal number => number.ToString("0.############################", CultureInfo.InvariantCulture),
            DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
            _ => Value.ToString() ?? string.Empty
        };

        public JsonNode? ToJsonNode() => Value switch
        {
            null => null,
            decimal number => JsonValue.Create(number),
            bool boolean => JsonValue.Create(boolean),
            DateTimeOffset date => JsonValue.Create(date.ToString("O", CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(ToDisplayText())
        };
    }

    private sealed class FormulaParser
    {
        private readonly string expression;
        private readonly Func<string, WikiComputedValue> resolveProperty;
        private readonly bool validationOnly;
        private int position;
        private Token current;

        public FormulaParser(
            string expression,
            Func<string, WikiComputedValue> resolveProperty,
            bool validationOnly = false)
        {
            this.expression = expression;
            this.resolveProperty = resolveProperty;
            this.validationOnly = validationOnly;
            current = NextToken();
        }

        public WikiComputedValue Parse()
        {
            var value = ParseOr();
            if (current.Kind != TokenKind.End)
            {
                throw Error($"Unexpected token '{current.Text}'");
            }
            return value;
        }

        private WikiComputedValue ParseOr()
        {
            var left = ParseAnd();
            while (IsIdentifier("or"))
            {
                Advance();
                var right = ParseAnd();
                left = WikiComputedValue.Boolean(left.AsBoolean() || right.AsBoolean());
            }
            return left;
        }

        private WikiComputedValue ParseAnd()
        {
            var left = ParseComparison();
            while (IsIdentifier("and"))
            {
                Advance();
                var right = ParseComparison();
                left = WikiComputedValue.Boolean(left.AsBoolean() && right.AsBoolean());
            }
            return left;
        }

        private WikiComputedValue ParseComparison()
        {
            var left = ParseAdditive();
            while (current.Kind is TokenKind.Equal or TokenKind.NotEqual or TokenKind.Greater or TokenKind.GreaterEqual
                   or TokenKind.Less or TokenKind.LessEqual)
            {
                var operation = current.Kind;
                Advance();
                var right = ParseAdditive();
                var comparison = Compare(left, right);
                left = WikiComputedValue.Boolean(operation switch
                {
                    TokenKind.Equal => comparison == 0,
                    TokenKind.NotEqual => comparison != 0,
                    TokenKind.Greater => comparison > 0,
                    TokenKind.GreaterEqual => comparison >= 0,
                    TokenKind.Less => comparison < 0,
                    _ => comparison <= 0
                });
            }
            return left;
        }

        private WikiComputedValue ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                var operation = current.Kind;
                Advance();
                var right = ParseMultiplicative();
                if (operation == TokenKind.Plus && (!left.TryAsNumber(out var leftNumber) || !right.TryAsNumber(out var rightNumber)))
                {
                    left = WikiComputedValue.Text(left.ToDisplayText() + right.ToDisplayText());
                }
                else
                {
                    RequireNumbers(left, right, out leftNumber, out rightNumber);
                    left = WikiComputedValue.Number(operation == TokenKind.Plus ? leftNumber + rightNumber : leftNumber - rightNumber);
                }
            }
            return left;
        }

        private WikiComputedValue ParseMultiplicative()
        {
            var left = ParseUnary();
            while (current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
            {
                var operation = current.Kind;
                Advance();
                var right = ParseUnary();
                RequireNumbers(left, right, out var leftNumber, out var rightNumber);
                if (operation is TokenKind.Slash or TokenKind.Percent && rightNumber == 0)
                {
                    throw new FormulaEvaluationException("#DIV/0!");
                }
                left = WikiComputedValue.Number(operation switch
                {
                    TokenKind.Star => leftNumber * rightNumber,
                    TokenKind.Slash => leftNumber / rightNumber,
                    _ => leftNumber % rightNumber
                });
            }
            return left;
        }

        private WikiComputedValue ParseUnary()
        {
            if (current.Kind == TokenKind.Minus)
            {
                Advance();
                var value = ParseUnary();
                if (!value.TryAsNumber(out var number)) throw Error("Unary minus requires a number");
                return WikiComputedValue.Number(-number);
            }
            if (IsIdentifier("not"))
            {
                Advance();
                return WikiComputedValue.Boolean(!ParseUnary().AsBoolean());
            }
            return ParsePower();
        }

        private WikiComputedValue ParsePower()
        {
            var left = ParsePrimary();
            if (current.Kind != TokenKind.Caret) return left;

            Advance();
            var right = ParseUnary();
            RequireNumbers(left, right, out var leftNumber, out var rightNumber);
            var result = Math.Pow((double)leftNumber, (double)rightNumber);
            if (double.IsNaN(result) || double.IsInfinity(result)) throw Error("Power result is outside the supported range");
            return WikiComputedValue.Number((decimal)result);
        }

        private WikiComputedValue ParsePrimary()
        {
            if (current.Kind == TokenKind.Number)
            {
                var value = decimal.Parse(current.Text, CultureInfo.InvariantCulture);
                Advance();
                return WikiComputedValue.Number(value);
            }
            if (current.Kind == TokenKind.String)
            {
                var value = current.Text;
                Advance();
                return WikiComputedValue.Text(value);
            }
            if (current.Kind == TokenKind.Property)
            {
                var name = current.Text;
                Advance();
                return resolveProperty(name);
            }
            if (current.Kind == TokenKind.Identifier)
            {
                var identifier = current.Text;
                Advance();
                if (string.Equals(identifier, "true", StringComparison.OrdinalIgnoreCase)) return WikiComputedValue.Boolean(true);
                if (string.Equals(identifier, "false", StringComparison.OrdinalIgnoreCase)) return WikiComputedValue.Boolean(false);
                return ParseFunction(identifier);
            }
            if (current.Kind == TokenKind.LeftParen)
            {
                Advance();
                var value = ParseOr();
                Expect(TokenKind.RightParen);
                return value;
            }
            throw Error("Expected a number, string, property, function, or parenthesized expression");
        }

        private WikiComputedValue ParseFunction(string name)
        {
            Expect(TokenKind.LeftParen);
            var arguments = new List<WikiComputedValue>();
            if (current.Kind != TokenKind.RightParen)
            {
                do
                {
                    arguments.Add(ParseOr());
                    if (current.Kind != TokenKind.Comma) break;
                    Advance();
                } while (true);
            }
            Expect(TokenKind.RightParen);

            var normalizedName = name.ToLowerInvariant();
            ValidateFunctionSignature(normalizedName, arguments.Count);
            // Syntax validation cannot know a referenced property's runtime type. A neutral
            // numeric placeholder lets valid nested expressions such as abs([Hours]) * 2 parse
            // without weakening function-name or argument-count validation.
            if (validationOnly) return WikiComputedValue.Number(0);

            try
            {
                return normalizedName switch
                {
                    "if" => arguments[0].AsBoolean() ? arguments[1] : arguments[2],
                    "and" => WikiComputedValue.Boolean(arguments.All(argument => argument.AsBoolean())),
                    "or" => WikiComputedValue.Boolean(arguments.Any(argument => argument.AsBoolean())),
                    "not" => WikiComputedValue.Boolean(!arguments[0].AsBoolean()),
                    "empty" => WikiComputedValue.Boolean(arguments[0].IsEmpty),
                    "round" =>
                        WikiComputedValue.Number(decimal.Round(
                            RequireNumber(arguments[0], name),
                            arguments.Count == 2 ? (int)RequireNumber(arguments[1], name) : 0,
                            MidpointRounding.AwayFromZero)),
                    "abs" => WikiComputedValue.Number(decimal.Abs(RequireNumber(arguments[0], name))),
                    "ceil" => WikiComputedValue.Number(decimal.Ceiling(RequireNumber(arguments[0], name))),
                    "floor" => WikiComputedValue.Number(decimal.Floor(RequireNumber(arguments[0], name))),
                    "min" => WikiComputedValue.Number(arguments.Select(argument => RequireNumber(argument, name)).Min()),
                    "max" => WikiComputedValue.Number(arguments.Select(argument => RequireNumber(argument, name)).Max()),
                    "pow" => EvaluatePower(arguments, name),
                    "concat" => WikiComputedValue.Text(string.Concat(arguments.Select(argument => argument.ToDisplayText()))),
                    "coalesce" => arguments.FirstOrDefault(argument => !argument.IsEmpty),
                    "length" => WikiComputedValue.Number(arguments[0].ToDisplayText().Length),
                    "lower" => WikiComputedValue.Text(arguments[0].ToDisplayText().ToLowerInvariant()),
                    "upper" => WikiComputedValue.Text(arguments[0].ToDisplayText().ToUpperInvariant()),
                    "trim" => WikiComputedValue.Text(arguments[0].ToDisplayText().Trim()),
                    "contains" => WikiComputedValue.Boolean(arguments[0].ToDisplayText().Contains(
                        arguments[1].ToDisplayText(), StringComparison.OrdinalIgnoreCase)),
                    "startswith" => WikiComputedValue.Boolean(arguments[0].ToDisplayText().StartsWith(
                        arguments[1].ToDisplayText(), StringComparison.OrdinalIgnoreCase)),
                    "endswith" => WikiComputedValue.Boolean(arguments[0].ToDisplayText().EndsWith(
                        arguments[1].ToDisplayText(), StringComparison.OrdinalIgnoreCase)),
                    "replace" => WikiComputedValue.Text(arguments[0].ToDisplayText().Replace(
                        arguments[1].ToDisplayText(), arguments[2].ToDisplayText(), StringComparison.OrdinalIgnoreCase)),
                    "now" => WikiComputedValue.Date(DateTimeOffset.UtcNow),
                    "today" => WikiComputedValue.Date(new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero)),
                    "dateadd" => WikiComputedValue.Date(AddDate(
                        RequireDate(arguments[0], name), RequireNumber(arguments[1], name), arguments[2].ToDisplayText())),
                    "datebetween" => WikiComputedValue.Number(DateBetween(
                        RequireDate(arguments[0], name), RequireDate(arguments[1], name), arguments[2].ToDisplayText())),
                    "formatdate" => WikiComputedValue.Text(RequireDate(arguments[0], name).ToString(
                        NormalizeDateFormat(arguments[1].ToDisplayText()), CultureInfo.InvariantCulture)),
                    _ => throw Error($"Unknown function: {name}")
                };
            }
            catch (FormulaEvaluationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException or FormatException)
            {
                throw Error($"Invalid arguments for {name}");
            }
        }

        private void ValidateFunctionSignature(string name, int count)
        {
            var valid = name switch
            {
                "if" or "replace" or "dateadd" or "datebetween" => count == 3,
                "round" => count is 1 or 2,
                "and" or "or" or "concat" or "coalesce" or "min" or "max" => count >= 1,
                "not" or "empty" or "abs" or "ceil" or "floor" or "length" or "lower" or "upper" or "trim" => count == 1,
                "pow" or "contains" or "startswith" or "endswith" or "formatdate" => count == 2,
                "now" or "today" => count == 0,
                _ => false
            };
            if (!valid) throw Error($"Unknown function or invalid arguments: {name}");
        }

        private WikiComputedValue EvaluatePower(IReadOnlyList<WikiComputedValue> arguments, string name)
        {
            var result = Math.Pow((double)RequireNumber(arguments[0], name), (double)RequireNumber(arguments[1], name));
            if (double.IsNaN(result) || double.IsInfinity(result)) throw Error("Power result is outside the supported range");
            return WikiComputedValue.Number((decimal)result);
        }

        private decimal RequireNumber(WikiComputedValue value, string functionName)
        {
            if (value.TryAsNumber(out var number)) return number;
            throw Error($"{functionName} requires numeric values");
        }

        private DateTimeOffset RequireDate(WikiComputedValue value, string functionName)
        {
            if (value.TryAsDate(out var date)) return date;
            throw Error($"{functionName} requires a date value");
        }

        private DateTimeOffset AddDate(DateTimeOffset date, decimal amount, string unit) => unit.Trim().ToLowerInvariant() switch
        {
            "year" or "years" => date.AddYears((int)amount),
            "quarter" or "quarters" => date.AddMonths((int)amount * 3),
            "month" or "months" => date.AddMonths((int)amount),
            "week" or "weeks" => date.AddDays((double)amount * 7),
            "day" or "days" => date.AddDays((double)amount),
            "hour" or "hours" => date.AddHours((double)amount),
            "minute" or "minutes" => date.AddMinutes((double)amount),
            _ => throw Error($"Unsupported date unit: {unit}")
        };

        private decimal DateBetween(DateTimeOffset end, DateTimeOffset start, string unit)
        {
            var difference = end - start;
            return unit.Trim().ToLowerInvariant() switch
            {
                "year" or "years" => decimal.Truncate((decimal)difference.TotalDays / 365.2425m),
                "quarter" or "quarters" => decimal.Truncate((decimal)difference.TotalDays / 91.310625m),
                "month" or "months" => decimal.Truncate((decimal)difference.TotalDays / 30.436875m),
                "week" or "weeks" => decimal.Truncate((decimal)difference.TotalDays / 7m),
                "day" or "days" => decimal.Truncate((decimal)difference.TotalDays),
                "hour" or "hours" => decimal.Truncate((decimal)difference.TotalHours),
                "minute" or "minutes" => decimal.Truncate((decimal)difference.TotalMinutes),
                _ => throw Error($"Unsupported date unit: {unit}")
            };
        }

        private static string NormalizeDateFormat(string format) => format
            .Replace("YYYY", "yyyy", StringComparison.Ordinal)
            .Replace("YY", "yy", StringComparison.Ordinal)
            .Replace("DD", "dd", StringComparison.Ordinal);

        private static int Compare(WikiComputedValue left, WikiComputedValue right)
        {
            if (left.TryAsNumber(out var leftNumber) && right.TryAsNumber(out var rightNumber))
            {
                return leftNumber.CompareTo(rightNumber);
            }
            if (left.TryAsDate(out var leftDate) && right.TryAsDate(out var rightDate))
            {
                return leftDate.CompareTo(rightDate);
            }
            return string.Compare(left.ToDisplayText(), right.ToDisplayText(), StringComparison.OrdinalIgnoreCase);
        }

        private void RequireNumbers(WikiComputedValue left, WikiComputedValue right, out decimal leftNumber, out decimal rightNumber)
        {
            if (!left.TryAsNumber(out leftNumber) || !right.TryAsNumber(out rightNumber))
            {
                throw Error("This operator requires numeric values");
            }
        }

        private void Expect(TokenKind kind)
        {
            if (current.Kind != kind) throw Error($"Expected {kind}");
            Advance();
        }

        private void Advance() => current = NextToken();

        private bool IsIdentifier(string value) => current.Kind == TokenKind.Identifier
            && string.Equals(current.Text, value, StringComparison.OrdinalIgnoreCase);

        private Token NextToken()
        {
            while (position < expression.Length && char.IsWhiteSpace(expression[position])) position++;
            if (position >= expression.Length) return new Token(TokenKind.End, string.Empty);

            var start = position;
            var character = expression[position++];
            if (char.IsDigit(character) || character == '.' && position < expression.Length && char.IsDigit(expression[position]))
            {
                while (position < expression.Length && (char.IsDigit(expression[position]) || expression[position] == '.')) position++;
                return new Token(TokenKind.Number, expression[start..position]);
            }
            if (character is '"' or '\'')
            {
                var quote = character;
                start = position;
                while (position < expression.Length && expression[position] != quote) position++;
                if (position >= expression.Length) throw Error("Unterminated string");
                var text = expression[start..position];
                position++;
                return new Token(TokenKind.String, text);
            }
            if (character == '[')
            {
                start = position;
                while (position < expression.Length && expression[position] != ']') position++;
                if (position >= expression.Length) throw Error("Unterminated property reference");
                var name = expression[start..position].Trim();
                position++;
                return new Token(TokenKind.Property, name);
            }
            if (char.IsLetter(character) || character == '_')
            {
                while (position < expression.Length && (char.IsLetterOrDigit(expression[position]) || expression[position] == '_')) position++;
                return new Token(TokenKind.Identifier, expression[start..position]);
            }

            return character switch
            {
                '+' => new Token(TokenKind.Plus, "+"),
                '-' => new Token(TokenKind.Minus, "-"),
                '*' => new Token(TokenKind.Star, "*"),
                '/' => new Token(TokenKind.Slash, "/"),
                '%' => new Token(TokenKind.Percent, "%"),
                '^' => new Token(TokenKind.Caret, "^"),
                '(' => new Token(TokenKind.LeftParen, "("),
                ')' => new Token(TokenKind.RightParen, ")"),
                ',' => new Token(TokenKind.Comma, ","),
                '=' when Match('=') => new Token(TokenKind.Equal, "=="),
                '!' when Match('=') => new Token(TokenKind.NotEqual, "!="),
                '>' when Match('=') => new Token(TokenKind.GreaterEqual, ">="),
                '>' => new Token(TokenKind.Greater, ">"),
                '<' when Match('=') => new Token(TokenKind.LessEqual, "<="),
                '<' => new Token(TokenKind.Less, "<"),
                _ => throw Error($"Unexpected character '{character}'")
            };
        }

        private bool Match(char expected)
        {
            if (position >= expression.Length || expression[position] != expected) return false;
            position++;
            return true;
        }

        private FormulaEvaluationException Error(string message) =>
            new($"#ERROR! {message} at position {Math.Max(0, position - 1)}");

        private readonly record struct Token(TokenKind Kind, string Text);
        private enum TokenKind
        {
            End, Number, String, Property, Identifier, Plus, Minus, Star, Slash, Percent, Caret,
            LeftParen, RightParen, Comma, Equal, NotEqual, Greater, GreaterEqual, Less, LessEqual
        }
    }

    private sealed class FormulaEvaluationException(string message) : Exception(message);
}
