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
        }).Parse();
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
            WikiDatabasePropertyTypes.CreatedTime => WikiComputedValue.Text(row.CreatedAt.ToString("O")),
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
            string text => !string.IsNullOrWhiteSpace(text) && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        public string ToDisplayText() => Value switch
        {
            null => string.Empty,
            bool boolean => boolean ? "True" : "False",
            decimal number => number.ToString("0.############################", CultureInfo.InvariantCulture),
            _ => Value.ToString() ?? string.Empty
        };

        public JsonNode? ToJsonNode() => Value switch
        {
            null => null,
            decimal number => JsonValue.Create(number),
            bool boolean => JsonValue.Create(boolean),
            _ => JsonValue.Create(ToDisplayText())
        };
    }

    private sealed class FormulaParser
    {
        private readonly string expression;
        private readonly Func<string, WikiComputedValue> resolveProperty;
        private int position;
        private Token current;

        public FormulaParser(string expression, Func<string, WikiComputedValue> resolveProperty)
        {
            this.expression = expression;
            this.resolveProperty = resolveProperty;
            current = NextToken();
        }

        public WikiComputedValue Parse()
        {
            var value = ParseComparison();
            if (current.Kind != TokenKind.End)
            {
                throw Error($"Unexpected token '{current.Text}'");
            }
            return value;
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
            while (current.Kind is TokenKind.Star or TokenKind.Slash)
            {
                var operation = current.Kind;
                Advance();
                var right = ParseUnary();
                RequireNumbers(left, right, out var leftNumber, out var rightNumber);
                if (operation == TokenKind.Slash && rightNumber == 0)
                {
                    throw new FormulaEvaluationException("#DIV/0!");
                }
                left = WikiComputedValue.Number(operation == TokenKind.Star ? leftNumber * rightNumber : leftNumber / rightNumber);
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
            return ParsePrimary();
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
                var value = ParseComparison();
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
                    arguments.Add(ParseComparison());
                    if (current.Kind != TokenKind.Comma) break;
                    Advance();
                } while (true);
            }
            Expect(TokenKind.RightParen);

            return name.ToLowerInvariant() switch
            {
                "if" when arguments.Count == 3 => arguments[0].AsBoolean() ? arguments[1] : arguments[2],
                "round" when arguments.Count is 1 or 2 && arguments[0].TryAsNumber(out var number) =>
                    WikiComputedValue.Number(decimal.Round(
                        number,
                        arguments.Count == 2 && arguments[1].TryAsNumber(out var places) ? (int)places : 0,
                        MidpointRounding.AwayFromZero)),
                "concat" => WikiComputedValue.Text(string.Concat(arguments.Select(argument => argument.ToDisplayText()))),
                "coalesce" => arguments.FirstOrDefault(argument => !argument.IsEmpty),
                _ => throw Error($"Unknown function or invalid arguments: {name}")
            };
        }

        private static int Compare(WikiComputedValue left, WikiComputedValue right)
        {
            if (left.TryAsNumber(out var leftNumber) && right.TryAsNumber(out var rightNumber))
            {
                return leftNumber.CompareTo(rightNumber);
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
            End, Number, String, Property, Identifier, Plus, Minus, Star, Slash,
            LeftParen, RightParen, Comma, Equal, NotEqual, Greater, GreaterEqual, Less, LessEqual
        }
    }

    private sealed class FormulaEvaluationException(string message) : Exception(message);
}
