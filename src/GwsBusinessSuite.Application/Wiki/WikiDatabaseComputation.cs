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
            // Same underlying count as CountValues - kept as a separate named aggregation
            // purely so the option list matches Notion's own naming (both exist there too).
            WikiDatabaseRollupAggregations.CountNotEmpty => WikiComputedValue.Number(values.Count),
            WikiDatabaseRollupAggregations.CountEmpty => WikiComputedValue.Number(relatedRows.Count - values.Count),
            WikiDatabaseRollupAggregations.PercentEmpty => WikiComputedValue.Number(
                relatedRows.Count == 0 ? 0 : Math.Round(100m * (relatedRows.Count - values.Count) / relatedRows.Count, 1)),
            WikiDatabaseRollupAggregations.PercentNotEmpty => WikiComputedValue.Number(
                relatedRows.Count == 0 ? 0 : Math.Round(100m * values.Count / relatedRows.Count, 1)),
            WikiDatabaseRollupAggregations.Median => AggregateNumbers(values, Median),
            WikiDatabaseRollupAggregations.Range => AggregateNumbers(values, numbers => numbers.Count == 0 ? 0 : numbers.Max() - numbers.Min()),
            _ => WikiComputedValue.Text("#ERROR! Unsupported rollup")
        };
    }

    private static decimal Median(IReadOnlyList<decimal> numbers)
    {
        if (numbers.Count == 0) return 0;
        var sorted = numbers.OrderBy(number => number).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
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
            WikiDatabasePropertyTypes.LastEditedTime => WikiComputedValue.Date(row.UpdatedAt ?? row.CreatedAt),
            WikiDatabasePropertyTypes.CreatedBy => WikiComputedValue.Text(row.CreatedBy),
            WikiDatabasePropertyTypes.LastEditedBy => WikiComputedValue.Text(row.UpdatedBy ?? row.CreatedBy),
            WikiDatabasePropertyTypes.UniqueId => WikiPropertyValues.GetNumber(values, property.Id) is { } uniqueIdNumber
                ? WikiComputedValue.Number(uniqueIdNumber) : WikiComputedValue.Empty,
            WikiDatabasePropertyTypes.Relation => RelatedRows(property, values, databases, evaluationPath),
            WikiDatabasePropertyTypes.MultiSelect or WikiDatabasePropertyTypes.Person or WikiDatabasePropertyTypes.Files =>
                WikiComputedValue.Array(WikiPropertyValues.GetMultiSelect(values, property.Id)
                    .Select(item => WikiComputedValue.Text(item)).ToList()),
            _ => WikiComputedValue.Text(WikiPropertyValues.GetDisplayText(property, values, row.CreatedAt, row.UpdatedAt, row.CreatedBy, row.UpdatedBy))
        };
    }

    // Each related row is a RowReference that prop() can read through; an id whose row isn't
    // loaded stays plain text, as every relation value was before.
    private static WikiComputedValue RelatedRows(
        WikiDatabaseProperty property,
        JsonObject values,
        IReadOnlyDictionary<Guid, WikiDatabase> databases,
        HashSet<(Guid RowId, Guid PropertyId)> evaluationPath)
    {
        var relatedDatabase = WikiDatabasePropertyConfig.Parse(property).RelatedDatabaseId is { } relatedDatabaseId
            && databases.TryGetValue(relatedDatabaseId, out var found) ? found : null;
        return WikiComputedValue.Array(WikiPropertyValues.GetMultiSelect(values, property.Id)
            .Select(storedId => relatedDatabase is not null && Guid.TryParse(storedId, out var rowId)
                && relatedDatabase.Rows.FirstOrDefault(row => row.Id == rowId) is { } relatedRow
                    ? new WikiComputedValue(new RowReference(storedId, propertyName =>
                    {
                        var target = relatedDatabase.Properties.FirstOrDefault(candidate =>
                            string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new FormulaEvaluationException($"#REF! Unknown property [{propertyName}] in {relatedDatabase.Title}");
                        return EvaluateProperty(relatedDatabase, relatedRow, target, databases, evaluationPath);
                    }))
                    : WikiComputedValue.Text(storedId))
            .ToList());
    }

    private readonly record struct WikiComputedValue(object? Value)
    {
        public static WikiComputedValue Empty => new(null);
        public static WikiComputedValue Number(decimal value) => new(value);
        public static WikiComputedValue Boolean(bool value) => new(value);
        public static WikiComputedValue Text(string? value) => new(value);
        public static WikiComputedValue Date(DateTimeOffset value) => new(value);
        // Backs MultiSelect/Person/Files/Relation property references and the new list/array
        // formula functions (length/first/last/at/slice/unique/join/includes/sort). Every
        // *other* existing formula behavior (concat, comparisons, string coercion) is
        // preserved unchanged because ToDisplayText() below still comma-joins an array
        // exactly like the plain-text join this replaced.
        public static WikiComputedValue Array(IReadOnlyList<WikiComputedValue> items) => new(items);
        public bool IsEmpty => Value switch
        {
            null => true,
            string text => string.IsNullOrWhiteSpace(text),
            IReadOnlyList<WikiComputedValue> items => items.Count == 0,
            _ => false
        };
        public bool IsArray => Value is IReadOnlyList<WikiComputedValue>;
        public IReadOnlyList<WikiComputedValue> AsArray() =>
            Value as IReadOnlyList<WikiComputedValue> ?? [this];

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
            RowReference => true,
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
            IReadOnlyList<WikiComputedValue> items => string.Join(", ", items.Select(item => item.ToDisplayText())),
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

    // Formulas are parsed into a tree first and evaluated afterwards (Sentinel item 31), so that
    // map/filter/find/some/every can run their second argument once per list item, with
    // `current` and `index` bound, and let/lets can bind names. Everything else is evaluated
    // exactly as the original single-pass parser did - every argument eagerly and in order (so
    // if(true, 1, 1/0) is still #DIV/0!), with each error reporting the same "at position N" -
    // and WikiFormulaCorpusTests pins that.
    private sealed class FormulaParser
    {
        private static readonly HashSet<string> ListFunctions = ["map", "filter", "find", "some", "every"];

        private readonly string expression;
        private readonly Func<string, WikiComputedValue> resolveProperty;
        private readonly bool validationOnly;
        private readonly List<string> scope = [];
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
            var tree = ParseOr();
            if (current.Kind != TokenKind.End)
            {
                throw Error($"Unexpected token '{current.Text}'");
            }
            return new Evaluator(resolveProperty, validationOnly).Evaluate(tree, Variables.None);
        }

        // ---- Parsing ----

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (IsIdentifier("or"))
            {
                Advance();
                left = new BinaryNode("or", left, ParseAnd(), ErrorPosition);
            }
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseComparison();
            while (IsIdentifier("and"))
            {
                Advance();
                left = new BinaryNode("and", left, ParseComparison(), ErrorPosition);
            }
            return left;
        }

        private Node ParseComparison()
        {
            var left = ParseAdditive();
            while (current.Kind is TokenKind.Equal or TokenKind.NotEqual or TokenKind.Greater or TokenKind.GreaterEqual
                   or TokenKind.Less or TokenKind.LessEqual)
            {
                var operation = current.Text;
                Advance();
                left = new BinaryNode(operation, left, ParseAdditive(), ErrorPosition);
            }
            return left;
        }

        private Node ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                var operation = current.Text;
                Advance();
                left = new BinaryNode(operation, left, ParseMultiplicative(), ErrorPosition);
            }
            return left;
        }

        private Node ParseMultiplicative()
        {
            var left = ParseUnary();
            while (current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
            {
                var operation = current.Text;
                Advance();
                left = new BinaryNode(operation, left, ParseUnary(), ErrorPosition);
            }
            return left;
        }

        private Node ParseUnary()
        {
            if (current.Kind == TokenKind.Minus)
            {
                Advance();
                var operand = ParseUnary();
                return new UnaryNode("-", operand, ErrorPosition);
            }
            if (IsIdentifier("not"))
            {
                Advance();
                return new UnaryNode("not", ParseUnary(), ErrorPosition);
            }
            return ParsePower();
        }

        private Node ParsePower()
        {
            var left = ParsePostfix();
            if (current.Kind != TokenKind.Caret) return left;

            Advance();
            var right = ParseUnary();
            return new BinaryNode("^", left, right, ErrorPosition);
        }

        // value.function(arguments) is the same call as function(value, arguments), so lists and
        // relations read left to right: prop("Client").prop("Email"), [Tags].filter(current != "x").
        private Node ParsePostfix()
        {
            var node = ParsePrimary();
            while (current.Kind == TokenKind.Dot)
            {
                Advance();
                if (current.Kind != TokenKind.Identifier) throw Error("Expected a function name after '.'");
                var name = current.Text;
                Advance();
                node = ParseFunction(name, node);
            }
            return node;
        }

        private Node ParsePrimary()
        {
            if (current.Kind == TokenKind.Number)
            {
                var value = decimal.Parse(current.Text, CultureInfo.InvariantCulture);
                Advance();
                return new LiteralNode(WikiComputedValue.Number(value));
            }
            if (current.Kind == TokenKind.String)
            {
                var value = current.Text;
                Advance();
                return new LiteralNode(WikiComputedValue.Text(value));
            }
            if (current.Kind == TokenKind.Property)
            {
                var name = current.Text;
                Advance();
                return new PropertyNode(name);
            }
            if (current.Kind == TokenKind.Identifier)
            {
                var identifier = current.Text;
                Advance();
                if (string.Equals(identifier, "true", StringComparison.OrdinalIgnoreCase)) return new LiteralNode(WikiComputedValue.Boolean(true));
                if (string.Equals(identifier, "false", StringComparison.OrdinalIgnoreCase)) return new LiteralNode(WikiComputedValue.Boolean(false));
                // A name bound by map/filter/... (current, index) or let/lets. Anything else must
                // be a function call - the same "Expected LeftParen" error as before.
                if (current.Kind != TokenKind.LeftParen && scope.Contains(identifier, StringComparer.OrdinalIgnoreCase))
                {
                    return new VariableNode(identifier);
                }
                return ParseFunction(identifier, receiver: null);
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

        private Node ParseFunction(string name, Node? receiver)
        {
            Expect(TokenKind.LeftParen);
            var normalizedName = name.ToLowerInvariant();
            if (receiver is null && normalizedName is "let" or "lets")
            {
                return ParseLet(normalizedName);
            }

            var arguments = new List<Node>();
            if (receiver is not null) arguments.Add(receiver);
            if (current.Kind != TokenKind.RightParen)
            {
                do
                {
                    // The second argument of a list function runs once per item.
                    var isItemExpression = ListFunctions.Contains(normalizedName) && arguments.Count == 1;
                    if (isItemExpression) scope.AddRange(["current", "index"]);
                    arguments.Add(ParseOr());
                    if (isItemExpression) scope.RemoveRange(scope.Count - 2, 2);
                    if (current.Kind != TokenKind.Comma) break;
                    Advance();
                } while (true);
            }
            Expect(TokenKind.RightParen);

            // A wrong name or argument count is reported when the call is evaluated, after its
            // arguments, which is the order the original parser reported it in.
            string? signatureError = null;
            try
            {
                ValidateFunctionSignature(normalizedName, arguments.Count);
            }
            catch (FormulaEvaluationException exception)
            {
                signatureError = exception.Message;
            }
            return new CallNode(name, normalizedName, arguments, receiver is not null, signatureError, ErrorPosition);
        }

        // let(name, value, expression) and lets(name1, value1, name2, value2, ..., expression).
        private Node ParseLet(string normalizedName)
        {
            var bindings = new List<(string Name, Node Value)>();
            var added = 0;
            do
            {
                if (current.Kind != TokenKind.Identifier) throw Error($"{normalizedName} needs a name to bind");
                var name = current.Text;
                Advance();
                Expect(TokenKind.Comma);
                bindings.Add((name, ParseOr()));
                scope.Add(name);
                added++;
                Expect(TokenKind.Comma);
            } while (normalizedName == "lets" && current.Kind == TokenKind.Identifier && PeekKind() == TokenKind.Comma);
            var body = ParseOr();
            Expect(TokenKind.RightParen);
            scope.RemoveRange(scope.Count - added, added);
            return new LetNode(bindings, body);
        }

        private void ValidateFunctionSignature(string name, int count)
        {
            var valid = name switch
            {
                "if" or "replace" or "dateadd" or "datebetween" or "datesubtract" => count == 3,
                "round" => count is 1 or 2,
                "and" or "or" or "concat" or "coalesce" or "min" or "max" or "sum" => count >= 1,
                "not" or "empty" or "abs" or "ceil" or "floor" or "length" or "lower" or "upper" or "trim"
                    or "first" or "last" or "unique" or "sort" or "year" or "month" or "date" or "day" or "hour"
                    or "minute" or "week" or "parsedate" => count == 1,
                "pow" or "contains" or "startswith" or "endswith" or "formatdate" or "at" or "includes"
                    or "map" or "filter" or "find" or "some" or "every" => count == 2,
                "slice" => count is 2 or 3,
                "prop" => count is 1 or 2,
                "join" => count is 1 or 2,
                "now" or "today" => count == 0,
                _ => false
            };
            if (!valid) throw Error($"Unknown function or invalid arguments: {name}");
        }

        private int ErrorPosition => Math.Max(0, position - 1);

        private void Expect(TokenKind kind)
        {
            if (current.Kind != kind) throw Error($"Expected {kind}");
            Advance();
        }

        private void Advance() => current = NextToken();

        private TokenKind PeekKind()
        {
            var (savedPosition, savedToken) = (position, current);
            var next = NextToken();
            (position, current) = (savedPosition, savedToken);
            return next.Kind;
        }

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
                '.' => new Token(TokenKind.Dot, "."),
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

        private FormulaEvaluationException Error(string message) => ErrorAt(Math.Max(0, position - 1), message);

        private readonly record struct Token(TokenKind Kind, string Text);
        private enum TokenKind
        {
            End, Number, String, Property, Identifier, Plus, Minus, Star, Slash, Percent, Caret,
            LeftParen, RightParen, Comma, Dot, Equal, NotEqual, Greater, GreaterEqual, Less, LessEqual
        }
    }

    private static FormulaEvaluationException ErrorAt(int position, string message) =>
        new($"#ERROR! {message} at position {position}");

    // ---- The parsed formula ----
    // Position is where the original parser stood when it would have reported an error here.

    private abstract record Node;
    private sealed record LiteralNode(WikiComputedValue Value) : Node;
    private sealed record PropertyNode(string Name) : Node;
    private sealed record VariableNode(string Name) : Node;
    private sealed record UnaryNode(string Operation, Node Operand, int Position) : Node;
    private sealed record BinaryNode(string Operation, Node Left, Node Right, int Position) : Node;
    private sealed record CallNode(string Name, string NormalizedName, IReadOnlyList<Node> Arguments, bool IsMethod, string? SignatureError, int Position) : Node;
    private sealed record LetNode(IReadOnlyList<(string Name, Node Value)> Bindings, Node Body) : Node;

    // Names bound by let/lets and the list functions, innermost first.
    private sealed record Variables(string Name, WikiComputedValue Value, Variables? Outer)
    {
        public static readonly Variables? None = null;

        public static WikiComputedValue Lookup(Variables? variables, string name)
        {
            for (var scope = variables; scope is not null; scope = scope.Outer)
            {
                if (string.Equals(scope.Name, name, StringComparison.OrdinalIgnoreCase)) return scope.Value;
            }
            return WikiComputedValue.Empty;
        }
    }

    private sealed class Evaluator(Func<string, WikiComputedValue> resolveProperty, bool validationOnly)
    {
        public WikiComputedValue Evaluate(Node node, Variables? variables) => node switch
        {
            LiteralNode literal => literal.Value,
            PropertyNode property => resolveProperty(property.Name),
            VariableNode variable => Variables.Lookup(variables, variable.Name),
            UnaryNode unary => EvaluateUnary(unary, variables),
            BinaryNode binary => EvaluateBinary(binary, variables),
            CallNode call => EvaluateCall(call, variables),
            LetNode let => EvaluateLet(let, variables),
            _ => throw new InvalidOperationException("Unknown formula node.")
        };

        private WikiComputedValue EvaluateUnary(UnaryNode node, Variables? variables)
        {
            var value = Evaluate(node.Operand, variables);
            if (node.Operation == "not") return WikiComputedValue.Boolean(!value.AsBoolean());
            if (!value.TryAsNumber(out var number)) throw ErrorAt(node.Position, "Unary minus requires a number");
            return WikiComputedValue.Number(-number);
        }

        private WikiComputedValue EvaluateBinary(BinaryNode node, Variables? variables)
        {
            var left = Evaluate(node.Left, variables);
            var right = Evaluate(node.Right, variables);
            switch (node.Operation)
            {
                case "or": return WikiComputedValue.Boolean(left.AsBoolean() || right.AsBoolean());
                case "and": return WikiComputedValue.Boolean(left.AsBoolean() && right.AsBoolean());
                case "==": return WikiComputedValue.Boolean(Compare(left, right) == 0);
                case "!=": return WikiComputedValue.Boolean(Compare(left, right) != 0);
                case ">": return WikiComputedValue.Boolean(Compare(left, right) > 0);
                case ">=": return WikiComputedValue.Boolean(Compare(left, right) >= 0);
                case "<": return WikiComputedValue.Boolean(Compare(left, right) < 0);
                case "<=": return WikiComputedValue.Boolean(Compare(left, right) <= 0);
            }
            if (node.Operation == "+" && (!left.TryAsNumber(out _) || !right.TryAsNumber(out _)))
            {
                return WikiComputedValue.Text(left.ToDisplayText() + right.ToDisplayText());
            }
            if (!left.TryAsNumber(out var leftNumber) || !right.TryAsNumber(out var rightNumber))
            {
                throw ErrorAt(node.Position, "This operator requires numeric values");
            }
            switch (node.Operation)
            {
                case "+": return WikiComputedValue.Number(leftNumber + rightNumber);
                case "-": return WikiComputedValue.Number(leftNumber - rightNumber);
                case "*": return WikiComputedValue.Number(leftNumber * rightNumber);
                case "/" or "%" when rightNumber == 0: throw new FormulaEvaluationException("#DIV/0!");
                case "/": return WikiComputedValue.Number(leftNumber / rightNumber);
                case "%": return WikiComputedValue.Number(leftNumber % rightNumber);
                default:
                    var result = Math.Pow((double)leftNumber, (double)rightNumber);
                    if (double.IsNaN(result) || double.IsInfinity(result)) throw ErrorAt(node.Position, "Power result is outside the supported range");
                    return WikiComputedValue.Number((decimal)result);
            }
        }

        private WikiComputedValue EvaluateLet(LetNode node, Variables? variables)
        {
            foreach (var (name, value) in node.Bindings)
            {
                variables = new Variables(name, Evaluate(value, variables), variables);
            }
            return Evaluate(node.Body, variables);
        }

        private WikiComputedValue EvaluateCall(CallNode node, Variables? variables)
        {
            var name = node.Name;
            var isListFunction = node.Arguments.Count == 2 && node.NormalizedName is "map" or "filter" or "find" or "some" or "every";
            // A list function's second argument is an expression to run per item, not a value.
            var arguments = node.Arguments
                .Take(isListFunction ? 1 : node.Arguments.Count)
                .Select(argument => Evaluate(argument, variables))
                .ToList();
            if (node.SignatureError is not null) throw new FormulaEvaluationException(node.SignatureError);
            // Syntax validation cannot know a referenced property's runtime type. A neutral
            // numeric placeholder lets valid nested expressions such as abs([Hours]) * 2 parse
            // without weakening function-name or argument-count validation.
            if (validationOnly)
            {
                if (isListFunction)
                {
                    Evaluate(node.Arguments[1], new Variables("current", WikiComputedValue.Number(0),
                        new Variables("index", WikiComputedValue.Number(0), variables)));
                }
                // prop("Name") on this row is a property reference, so check the name like [Name].
                if (node.NormalizedName == "prop" && arguments.Count == 1) resolveProperty(arguments[0].ToDisplayText());
                return WikiComputedValue.Number(0);
            }

            try
            {
                if (isListFunction) return EvaluateListFunction(node, arguments[0].AsArray(), variables);
                return node.NormalizedName switch
                {
                    "if" => arguments[0].AsBoolean() ? arguments[1] : arguments[2],
                    "and" => WikiComputedValue.Boolean(arguments.All(argument => argument.AsBoolean())),
                    "or" => WikiComputedValue.Boolean(arguments.Any(argument => argument.AsBoolean())),
                    "not" => WikiComputedValue.Boolean(!arguments[0].AsBoolean()),
                    "empty" => WikiComputedValue.Boolean(arguments[0].IsEmpty),
                    "round" =>
                        WikiComputedValue.Number(decimal.Round(
                            RequireNumber(arguments[0], node),
                            arguments.Count == 2 ? (int)RequireNumber(arguments[1], node) : 0,
                            MidpointRounding.AwayFromZero)),
                    "abs" => WikiComputedValue.Number(decimal.Abs(RequireNumber(arguments[0], node))),
                    "ceil" => WikiComputedValue.Number(decimal.Ceiling(RequireNumber(arguments[0], node))),
                    "floor" => WikiComputedValue.Number(decimal.Floor(RequireNumber(arguments[0], node))),
                    "min" => WikiComputedValue.Number(arguments.Select(argument => RequireNumber(argument, node)).Min()),
                    "max" => WikiComputedValue.Number(arguments.Select(argument => RequireNumber(argument, node)).Max()),
                    // Lists are added up item by item; empty items are skipped.
                    "sum" => WikiComputedValue.Number(arguments.SelectMany(argument => argument.AsArray())
                        .Where(item => !item.IsEmpty).Sum(item => RequireNumber(item, node))),
                    "pow" => EvaluatePower(arguments, node),
                    "concat" => WikiComputedValue.Text(string.Concat(arguments.Select(argument => argument.ToDisplayText()))),
                    "coalesce" => arguments.FirstOrDefault(argument => !argument.IsEmpty),
                    // Polymorphic: an array argument (Relation/MultiSelect/Person/Files, or the
                    // result of another list function) counts elements; anything else falls
                    // back to the original string-length behavior.
                    "length" => WikiComputedValue.Number(
                        arguments[0].IsArray ? arguments[0].AsArray().Count : arguments[0].ToDisplayText().Length),
                    "first" => arguments[0].AsArray() is { Count: > 0 } firstItems ? firstItems[0] : WikiComputedValue.Empty,
                    "last" => arguments[0].AsArray() is { Count: > 0 } lastItems ? lastItems[^1] : WikiComputedValue.Empty,
                    "at" => AtIndex(arguments[0].AsArray(), (int)RequireNumber(arguments[1], node)),
                    "slice" => WikiComputedValue.Array(SliceArray(
                        arguments[0].AsArray(), (int)RequireNumber(arguments[1], node),
                        arguments.Count == 3 ? (int)RequireNumber(arguments[2], node) : null)),
                    "unique" => WikiComputedValue.Array(arguments[0].AsArray()
                        .DistinctBy(item => item.ToDisplayText(), StringComparer.OrdinalIgnoreCase).ToList()),
                    "join" => WikiComputedValue.Text(string.Join(
                        arguments.Count == 2 ? arguments[1].ToDisplayText() : ", ",
                        arguments[0].AsArray().Select(item => item.ToDisplayText()))),
                    "includes" => WikiComputedValue.Boolean(arguments[0].AsArray().Any(item =>
                        string.Equals(item.ToDisplayText(), arguments[1].ToDisplayText(), StringComparison.OrdinalIgnoreCase))),
                    "sort" => WikiComputedValue.Array(arguments[0].AsArray()
                        .OrderBy(item => item, Comparer<WikiComputedValue>.Create(Compare)).ToList()),
                    "prop" => EvaluateProp(node, arguments),
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
                        RequireDate(arguments[0], node), RequireNumber(arguments[1], node), arguments[2].ToDisplayText(), node)),
                    "datesubtract" => WikiComputedValue.Date(AddDate(
                        RequireDate(arguments[0], node), -RequireNumber(arguments[1], node), arguments[2].ToDisplayText(), node)),
                    "datebetween" => WikiComputedValue.Number(DateBetween(
                        RequireDate(arguments[0], node), RequireDate(arguments[1], node), arguments[2].ToDisplayText(), node)),
                    "formatdate" => WikiComputedValue.Text(RequireDate(arguments[0], node).ToString(
                        NormalizeDateFormat(arguments[1].ToDisplayText()), CultureInfo.InvariantCulture)),
                    "parsedate" => WikiComputedValue.Date(RequireDate(arguments[0], node)),
                    "year" => WikiComputedValue.Number(RequireDate(arguments[0], node).Year),
                    "month" => WikiComputedValue.Number(RequireDate(arguments[0], node).Month),
                    "date" => WikiComputedValue.Number(RequireDate(arguments[0], node).Day),
                    // ISO weekday: Monday 1 ... Sunday 7.
                    "day" => WikiComputedValue.Number(((int)RequireDate(arguments[0], node).DayOfWeek + 6) % 7 + 1),
                    "week" => WikiComputedValue.Number(ISOWeek.GetWeekOfYear(RequireDate(arguments[0], node).DateTime)),
                    "hour" => WikiComputedValue.Number(RequireDate(arguments[0], node).Hour),
                    "minute" => WikiComputedValue.Number(RequireDate(arguments[0], node).Minute),
                    _ => throw ErrorAt(node.Position, $"Unknown function: {name}")
                };
            }
            catch (FormulaEvaluationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException or FormatException)
            {
                throw ErrorAt(node.Position, $"Invalid arguments for {name}");
            }
        }

        // map/filter/find/some/every: the item expression sees `current` (the item) and `index`
        // (0-based).
        private WikiComputedValue EvaluateListFunction(CallNode node, IReadOnlyList<WikiComputedValue> items, Variables? variables)
        {
            WikiComputedValue Run(int index) => Evaluate(node.Arguments[1],
                new Variables("current", items[index], new Variables("index", WikiComputedValue.Number(index), variables)));
            var indexes = Enumerable.Range(0, items.Count);
            return node.NormalizedName switch
            {
                "map" => WikiComputedValue.Array(indexes.Select(Run).ToList()),
                "filter" => WikiComputedValue.Array(indexes.Where(index => Run(index).AsBoolean()).Select(index => items[index]).ToList()),
                "find" => indexes.Where(index => Run(index).AsBoolean()).Select(index => items[index]).FirstOrDefault(),
                "some" => WikiComputedValue.Boolean(indexes.Any(index => Run(index).AsBoolean())),
                _ => WikiComputedValue.Boolean(indexes.All(index => Run(index).AsBoolean()))
            };
        }

        // prop("Name") reads this row; relation.prop("Name") reads the related row(s) - a list
        // of related rows gives a list of values.
        private WikiComputedValue EvaluateProp(CallNode node, IReadOnlyList<WikiComputedValue> arguments)
        {
            if (arguments.Count == 1) return resolveProperty(arguments[0].ToDisplayText());
            var propertyName = arguments[1].ToDisplayText();
            // Nothing to read from (an empty relation's first(), say) reads as nothing.
            WikiComputedValue Read(WikiComputedValue item) => item.Value is RowReference row
                ? row.Resolve(propertyName)
                : item.IsEmpty ? WikiComputedValue.Empty
                : throw ErrorAt(node.Position, $"{node.Name} needs a relation to read [{propertyName}] from");
            return arguments[0].IsArray
                ? WikiComputedValue.Array(arguments[0].AsArray().Select(Read).ToList())
                : Read(arguments[0]);
        }

        private static WikiComputedValue EvaluatePower(IReadOnlyList<WikiComputedValue> arguments, CallNode node)
        {
            var result = Math.Pow((double)RequireNumber(arguments[0], node), (double)RequireNumber(arguments[1], node));
            if (double.IsNaN(result) || double.IsInfinity(result)) throw ErrorAt(node.Position, "Power result is outside the supported range");
            return WikiComputedValue.Number((decimal)result);
        }

        private static WikiComputedValue AtIndex(IReadOnlyList<WikiComputedValue> items, int index)
        {
            // Negative indexes count from the end, matching Notion's own at() semantics.
            var resolved = index < 0 ? items.Count + index : index;
            return resolved >= 0 && resolved < items.Count ? items[resolved] : WikiComputedValue.Empty;
        }

        private static List<WikiComputedValue> SliceArray(IReadOnlyList<WikiComputedValue> items, int start, int? end)
        {
            var from = Math.Clamp(start < 0 ? items.Count + start : start, 0, items.Count);
            var to = end is { } exclusiveEnd
                ? Math.Clamp(exclusiveEnd < 0 ? items.Count + exclusiveEnd : exclusiveEnd, 0, items.Count)
                : items.Count;
            return to > from ? items.Skip(from).Take(to - from).ToList() : [];
        }

        private static decimal RequireNumber(WikiComputedValue value, CallNode node)
        {
            if (value.TryAsNumber(out var number)) return number;
            throw ErrorAt(node.Position, $"{node.Name} requires numeric values");
        }

        private static DateTimeOffset RequireDate(WikiComputedValue value, CallNode node)
        {
            if (value.TryAsDate(out var date)) return date;
            throw ErrorAt(node.Position, $"{node.Name} requires a date value");
        }

        private static DateTimeOffset AddDate(DateTimeOffset date, decimal amount, string unit, CallNode node) => unit.Trim().ToLowerInvariant() switch
        {
            "year" or "years" => date.AddYears((int)amount),
            "quarter" or "quarters" => date.AddMonths((int)amount * 3),
            "month" or "months" => date.AddMonths((int)amount),
            "week" or "weeks" => date.AddDays((double)amount * 7),
            "day" or "days" => date.AddDays((double)amount),
            "hour" or "hours" => date.AddHours((double)amount),
            "minute" or "minutes" => date.AddMinutes((double)amount),
            _ => throw ErrorAt(node.Position, $"Unsupported date unit: {unit}")
        };

        private static decimal DateBetween(DateTimeOffset end, DateTimeOffset start, string unit, CallNode node)
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
                _ => throw ErrorAt(node.Position, $"Unsupported date unit: {unit}")
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
    }

    // A related row, as a formula sees a Relation value. It displays, compares and joins as the
    // stored row id - exactly the text it was before relations could be traversed - and
    // prop() reads its properties.
    private sealed class RowReference(string storedId, Func<string, WikiComputedValue> resolve)
    {
        public WikiComputedValue Resolve(string propertyName) => resolve(propertyName);
        public override string ToString() => storedId;
    }

    private sealed class FormulaEvaluationException(string message) : Exception(message);
}
