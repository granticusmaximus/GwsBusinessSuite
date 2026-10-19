using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Automation;

// The {{ ... }} expression language used in node parameters. A small, safe language: values,
// paths, functions, arithmetic and comparisons - no loops, assignment or access to anything
// outside the workflow's own data, and a cap on length and nesting, so an expression can't hang
// or reach the server.
//
//   {{ $json.order.total }}                     a field of this node's input
//   {{ $json.items[0].name }}                   array indexing
//   {{ $node("Fetch").json.status }}            an earlier node's output, by name
//   {{ upper($json.name) }}                     functions (see Functions below)
//   {{ $json.price * $json.qty }}               + - * / %, == != < > <= >=, && || !
//   {{ if($json.total > 100, 'big', 'small') }}
//
// A path segment may contain letters, digits, _ and - ($json.content-type); write subtraction
// with spaces ($json.a - 1). Text inside {{ }} that isn't a valid expression is left exactly as
// written, so literal templates meant for other systems ({{name}}) pass through untouched.
public static partial class AutomationExpressions
{
    private const int MaxExpressionLength = 2000;
    private const int MaxDepth = 64;

    public static readonly IReadOnlyList<string> Functions =
    [
        "upper", "lower", "trim", "length", "replace", "substring", "contains", "startsWith", "endsWith",
        "split", "join", "slugify", "urlEncode", "default", "coalesce", "concat", "string", "number",
        "round", "floor", "ceil", "abs", "min", "max", "now", "formatDate", "addDays", "if", "isEmpty",
        "json", "parseJson", "keys", "first", "last"
    ];

    // Replaces every {{ expression }} in the text with its value.
    public static string Render(string template, JsonElement input, IReadOnlyDictionary<string, JsonElement>? nodeOutputsByName = null)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{", StringComparison.Ordinal)) return template;
        return TemplatePattern().Replace(template, match =>
        {
            var source = match.Groups["expr"].Value;
            Node expression;
            try { expression = Parse(source); }
            catch (ExpressionException) { return match.Value; }
            try
            {
                return ToText(expression.Evaluate(new Scope(input, nodeOutputsByName)));
            }
            catch (ExpressionException ex)
            {
                throw new InvalidOperationException($"Expression {match.Value}: {ex.Message}");
            }
        });
    }

    // Syntax problems in text that's clearly meant as an expression (mentions $json, $node or
    // calls a function) - for workflow validation. Literal {{name}} templates aren't reported.
    public static IReadOnlyList<string> FindErrors(string template)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(template) || !template.Contains("{{", StringComparison.Ordinal)) return errors;
        foreach (Match match in TemplatePattern().Matches(template))
        {
            var source = match.Groups["expr"].Value;
            if (!LooksIntended().IsMatch(source)) continue;
            try { Parse(source); }
            catch (ExpressionException ex) { errors.Add($"{match.Value}: {ex.Message}"); }
        }
        return errors;
    }

    // Value -> text: strings as-is, numbers invariant, null/missing empty, objects/arrays as JSON.
    private static string ToText(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
        // Numbers as written in the source JSON (1.50 stays 1.50, as plain paths always gave).
        _ => value.ToJsonString()
    };

    // ---------------------------------------------------------------- parsing

    private static Node Parse(string source)
    {
        if (source.Length > MaxExpressionLength) throw new ExpressionException($"is longer than {MaxExpressionLength} characters");
        var parser = new Parser(Tokenize(source));
        var node = parser.ParseExpression(0);
        parser.ExpectEnd();
        return node;
    }

    private enum TokenKind { Number, String, Identifier, Variable, Operator, LeftParen, RightParen, LeftBracket, RightBracket, Comma, Dot, End }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);

    private static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var start = i;
            if (char.IsDigit(c))
            {
                while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.' && i + 1 < source.Length && char.IsDigit(source[i + 1]))) i++;
                tokens.Add(new(TokenKind.Number, source[start..i], start));
            }
            else if (c is '"' or '\'')
            {
                var text = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= source.Length) throw new ExpressionException($"has an unclosed {c} string");
                    if (source[i] == '\\' && i + 1 < source.Length) { text.Append(source[i + 1] switch { 'n' => '\n', 't' => '\t', var other => other }); i += 2; continue; }
                    if (source[i] == c) { i++; break; }
                    text.Append(source[i++]);
                }
                tokens.Add(new(TokenKind.String, text.ToString(), start));
            }
            else if (c == '$' || char.IsLetter(c) || c == '_')
            {
                i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                tokens.Add(new(c == '$' ? TokenKind.Variable : TokenKind.Identifier, source[start..i], start));
            }
            else if (c == '.')
            {
                tokens.Add(new(TokenKind.Dot, ".", i++));
                // A path segment: letters, digits, _ and - (so $json.content-type and $json.0 work).
                var segmentStart = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '_' or '-')) i++;
                if (i > segmentStart) tokens.Add(new(TokenKind.Identifier, source[segmentStart..i], segmentStart));
            }
            else
            {
                var two = i + 1 < source.Length ? source.Substring(i, 2) : string.Empty;
                if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||") { tokens.Add(new(TokenKind.Operator, two, i)); i += 2; continue; }
                var kind = c switch
                {
                    '(' => TokenKind.LeftParen, ')' => TokenKind.RightParen, '[' => TokenKind.LeftBracket, ']' => TokenKind.RightBracket,
                    ',' => TokenKind.Comma, '+' or '-' or '*' or '/' or '%' or '<' or '>' or '!' => TokenKind.Operator,
                    _ => throw new ExpressionException($"has an unexpected '{c}'")
                };
                tokens.Add(new(kind, c.ToString(), i++));
            }
        }
        tokens.Add(new(TokenKind.End, string.Empty, source.Length));
        return tokens;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _index;
        private int _depth;

        private Token Peek => tokens[_index];
        private Token Next() => tokens[_index++];

        private static readonly Dictionary<string, int> Precedence = new()
        {
            ["||"] = 1, ["&&"] = 2, ["=="] = 3, ["!="] = 3, ["<"] = 4, [">"] = 4, ["<="] = 4, [">="] = 4,
            ["+"] = 5, ["-"] = 5, ["*"] = 6, ["/"] = 6, ["%"] = 6
        };

        public void ExpectEnd()
        {
            if (Peek.Kind != TokenKind.End) throw new ExpressionException($"has unexpected '{Peek.Text}' at position {Peek.Position + 1}");
        }

        public Node ParseExpression(int minPrecedence)
        {
            if (++_depth > MaxDepth) throw new ExpressionException("is nested too deeply");
            var left = ParseUnary();
            while (Peek.Kind == TokenKind.Operator && Precedence.TryGetValue(Peek.Text, out var precedence) && precedence > minPrecedence)
            {
                var op = Next().Text;
                var right = ParseExpression(precedence);
                left = new BinaryNode(op, left, right);
            }
            _depth--;
            return left;
        }

        private Node ParseUnary()
        {
            if (Peek.Kind == TokenKind.Operator && Peek.Text is "!" or "-")
            {
                var op = Next().Text;
                if (++_depth > MaxDepth) throw new ExpressionException("is nested too deeply");
                var operand = ParseUnary();
                _depth--;
                return new UnaryNode(op, operand);
            }
            return ParsePostfix(ParsePrimary());
        }

        private Node ParsePrimary()
        {
            var token = Next();
            switch (token.Kind)
            {
                case TokenKind.Number:
                    return new LiteralNode(JsonValue.Create(decimal.Parse(token.Text, CultureInfo.InvariantCulture)));
                case TokenKind.String:
                    return new LiteralNode(JsonValue.Create(token.Text));
                case TokenKind.LeftParen:
                    var inner = ParseExpression(0);
                    Expect(TokenKind.RightParen, ")");
                    return inner;
                case TokenKind.Variable when token.Text == "$json":
                    return new InputNode();
                case TokenKind.Variable when token.Text == "$node":
                    Expect(TokenKind.LeftParen, "(");
                    var name = Next();
                    if (name.Kind != TokenKind.String) throw new ExpressionException("$node needs a node name in quotes, like $node(\"Fetch\")");
                    Expect(TokenKind.RightParen, ")");
                    // The established form is $node("Name").json.path; ".json" is optional.
                    if (Peek.Kind == TokenKind.Dot && tokens[_index + 1] is { Kind: TokenKind.Identifier, Text: "json" }) _index += 2;
                    return new NodeOutputNode(name.Text);
                case TokenKind.Variable:
                    throw new ExpressionException($"doesn't know {token.Text} (use $json or $node(\"Name\"))");
                case TokenKind.Identifier when token.Text is "true" or "false":
                    return new LiteralNode(JsonValue.Create(token.Text == "true"));
                case TokenKind.Identifier when token.Text == "null":
                    return new LiteralNode(null);
                case TokenKind.Identifier when Peek.Kind == TokenKind.LeftParen:
                    if (!Functions.Contains(token.Text)) throw new ExpressionException($"has no function {token.Text}()");
                    Next();
                    var args = new List<Node>();
                    if (Peek.Kind != TokenKind.RightParen)
                    {
                        do { args.Add(ParseExpression(0)); } while (Peek.Kind == TokenKind.Comma && Next().Kind == TokenKind.Comma);
                    }
                    Expect(TokenKind.RightParen, ")");
                    return new CallNode(token.Text, args);
                case TokenKind.End:
                    throw new ExpressionException("is incomplete");
                default:
                    throw new ExpressionException($"has unexpected '{token.Text}' at position {token.Position + 1}");
            }
        }

        private Node ParsePostfix(Node target)
        {
            while (true)
            {
                if (Peek.Kind == TokenKind.Dot)
                {
                    Next();
                    var member = Next();
                    if (member.Kind != TokenKind.Identifier) throw new ExpressionException("needs a field name after '.'");
                    target = new MemberNode(target, new LiteralNode(JsonValue.Create(member.Text)));
                }
                else if (Peek.Kind == TokenKind.LeftBracket)
                {
                    Next();
                    var index = ParseExpression(0);
                    Expect(TokenKind.RightBracket, "]");
                    target = new MemberNode(target, index);
                }
                else return target;
            }
        }

        private void Expect(TokenKind kind, string text)
        {
            if (Next().Kind != kind) throw new ExpressionException($"is missing '{text}'");
        }
    }

    // ---------------------------------------------------------------- evaluation

    private sealed record Scope(JsonElement Input, IReadOnlyDictionary<string, JsonElement>? Nodes);

    private abstract record Node
    {
        public abstract JsonNode? Evaluate(Scope scope);
    }

    private sealed record LiteralNode(JsonNode? Value) : Node
    {
        public override JsonNode? Evaluate(Scope scope) => Value?.DeepClone();
    }

    private sealed record InputNode : Node
    {
        public override JsonNode? Evaluate(Scope scope) =>
            scope.Input.ValueKind is JsonValueKind.Undefined ? null : JsonNode.Parse(scope.Input.GetRawText());
    }

    private sealed record NodeOutputNode(string Name) : Node
    {
        public override JsonNode? Evaluate(Scope scope) =>
            scope.Nodes is not null && scope.Nodes.TryGetValue(Name, out var output) ? JsonNode.Parse(output.GetRawText()) : null;
    }

    private sealed record MemberNode(Node Target, Node Key) : Node
    {
        public override JsonNode? Evaluate(Scope scope)
        {
            var target = Target.Evaluate(scope);
            var key = Key.Evaluate(scope);
            return target switch
            {
                JsonObject obj => obj[ToText(key)]?.DeepClone(),
                JsonArray array when TryIndex(key, array.Count, out var index) => array[index]?.DeepClone(),
                // Missing paths read as empty, as they always have.
                _ => null
            };
        }

        private static bool TryIndex(JsonNode? key, int count, out int index)
        {
            index = -1;
            if (!decimal.TryParse(ToText(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return false;
            index = (int)number < 0 ? count + (int)number : (int)number;
            return index >= 0 && index < count;
        }
    }

    private sealed record UnaryNode(string Op, Node Operand) : Node
    {
        public override JsonNode? Evaluate(Scope scope)
        {
            var value = Operand.Evaluate(scope);
            return Op == "!" ? JsonValue.Create(!Truthy(value)) : JsonValue.Create(-RequireNumber(value, "-"));
        }
    }

    private sealed record BinaryNode(string Op, Node Left, Node Right) : Node
    {
        public override JsonNode? Evaluate(Scope scope)
        {
            // && and || stop early, like everywhere else.
            if (Op == "&&") return JsonValue.Create(Truthy(Left.Evaluate(scope)) && Truthy(Right.Evaluate(scope)));
            if (Op == "||") return JsonValue.Create(Truthy(Left.Evaluate(scope)) || Truthy(Right.Evaluate(scope)));
            var left = Left.Evaluate(scope);
            var right = Right.Evaluate(scope);
            switch (Op)
            {
                case "+":
                    return IsNumber(left) && IsNumber(right)
                        ? JsonValue.Create(Number(left)!.Value + Number(right)!.Value)
                        : JsonValue.Create(ToText(left) + ToText(right));
                case "-": return JsonValue.Create(RequireNumber(left, "-") - RequireNumber(right, "-"));
                case "*": return JsonValue.Create(RequireNumber(left, "*") * RequireNumber(right, "*"));
                case "/":
                case "%":
                    var divisor = RequireNumber(right, Op);
                    if (divisor == 0) throw new ExpressionException("divides by zero");
                    return JsonValue.Create(Op == "/" ? RequireNumber(left, Op) / divisor : RequireNumber(left, Op) % divisor);
                case "==": return JsonValue.Create(Compare(left, right) == 0);
                case "!=": return JsonValue.Create(Compare(left, right) != 0);
                case "<": return JsonValue.Create(Compare(left, right) < 0);
                case ">": return JsonValue.Create(Compare(left, right) > 0);
                case "<=": return JsonValue.Create(Compare(left, right) <= 0);
                case ">=": return JsonValue.Create(Compare(left, right) >= 0);
                default: throw new ExpressionException($"doesn't support '{Op}'");
            }
        }
    }

    private sealed record CallNode(string Name, IReadOnlyList<Node> Args) : Node
    {
        public override JsonNode? Evaluate(Scope scope)
        {
            // if() only evaluates the branch it takes.
            if (Name == "if")
            {
                Arity(2, 3);
                return Truthy(Args[0].Evaluate(scope)) ? Args[1].Evaluate(scope) : Args.Count > 2 ? Args[2].Evaluate(scope) : null;
            }
            var values = Args.Select(arg => arg.Evaluate(scope)).ToList();
            string Text(int i) => ToText(values[i]);
            switch (Name)
            {
                case "upper": Arity(1); return JsonValue.Create(Text(0).ToUpperInvariant());
                case "lower": Arity(1); return JsonValue.Create(Text(0).ToLowerInvariant());
                case "trim": Arity(1); return JsonValue.Create(Text(0).Trim());
                case "length":
                    Arity(1);
                    return JsonValue.Create(values[0] switch { JsonArray a => a.Count, JsonObject o => o.Count, null => 0, var v => ToText(v).Length });
                case "replace": Arity(3); return JsonValue.Create(Text(1).Length == 0 ? Text(0) : Text(0).Replace(Text(1), Text(2), StringComparison.Ordinal));
                case "substring":
                {
                    Arity(2, 3);
                    var text = Text(0);
                    var start = Math.Clamp((int)RequireNumber(values[1], "substring"), 0, text.Length);
                    var length = Args.Count > 2 ? Math.Clamp((int)RequireNumber(values[2], "substring"), 0, text.Length - start) : text.Length - start;
                    return JsonValue.Create(text.Substring(start, length));
                }
                case "contains":
                    Arity(2);
                    return JsonValue.Create(values[0] is JsonArray list
                        ? list.Any(item => Compare(item, values[1]) == 0)
                        : Text(0).Contains(Text(1), StringComparison.OrdinalIgnoreCase));
                case "startsWith": Arity(2); return JsonValue.Create(Text(0).StartsWith(Text(1), StringComparison.OrdinalIgnoreCase));
                case "endsWith": Arity(2); return JsonValue.Create(Text(0).EndsWith(Text(1), StringComparison.OrdinalIgnoreCase));
                case "split":
                    Arity(2);
                    return new JsonArray(Text(0).Split(Text(1).Length == 0 ? "," : Text(1)).Select(part => (JsonNode?)JsonValue.Create(part)).ToArray());
                case "join":
                    Arity(1, 2);
                    return JsonValue.Create(values[0] is JsonArray items
                        ? string.Join(Args.Count > 1 ? Text(1) : ", ", items.Select(ToText))
                        : Text(0));
                case "slugify": Arity(1); return JsonValue.Create(SlugPattern().Replace(Text(0).ToLowerInvariant(), "-").Trim('-'));
                case "urlEncode": Arity(1); return JsonValue.Create(Uri.EscapeDataString(Text(0)));
                case "default":
                    Arity(2);
                    return IsEmptyValue(values[0]) ? values[1] : values[0];
                case "coalesce":
                    return values.FirstOrDefault(value => !IsEmptyValue(value));
                case "concat": return JsonValue.Create(string.Concat(values.Select(ToText)));
                case "string": Arity(1); return JsonValue.Create(Text(0));
                case "number":
                    Arity(1);
                    return Number(values[0]) is { } parsed ? JsonValue.Create(parsed) : null;
                case "round":
                    Arity(1, 2);
                    return JsonValue.Create(Math.Round(RequireNumber(values[0], "round"), Args.Count > 1 ? Math.Clamp((int)RequireNumber(values[1], "round"), 0, 10) : 0, MidpointRounding.AwayFromZero));
                case "floor": Arity(1); return JsonValue.Create(Math.Floor(RequireNumber(values[0], "floor")));
                case "ceil": Arity(1); return JsonValue.Create(Math.Ceiling(RequireNumber(values[0], "ceil")));
                case "abs": Arity(1); return JsonValue.Create(Math.Abs(RequireNumber(values[0], "abs")));
                case "min":
                case "max":
                {
                    var numbers = (values.Count == 1 && values[0] is JsonArray array ? array.ToList() : values).Select(value => RequireNumber(value, Name)).ToList();
                    if (numbers.Count == 0) throw new ExpressionException($"{Name}() needs at least one number");
                    return JsonValue.Create(Name == "min" ? numbers.Min() : numbers.Max());
                }
                case "now": Arity(0); return JsonValue.Create(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                case "formatDate":
                {
                    Arity(1, 3);
                    var date = RequireDate(values[0]);
                    var zone = Args.Count > 2 ? FindZone(Text(2)) : TimeZoneInfo.Local;
                    var format = Args.Count > 1 && Text(1).Length > 0 ? Text(1) : "dd-MM-yy HH:mm";
                    try { return JsonValue.Create(TimeZoneInfo.ConvertTime(date, zone).ToString(format, CultureInfo.InvariantCulture)); }
                    catch (FormatException) { throw new ExpressionException($"formatDate() got an invalid format '{format}'"); }
                }
                case "addDays":
                    Arity(2);
                    return JsonValue.Create(RequireDate(values[0]).AddDays((double)RequireNumber(values[1], "addDays")).ToString("o", CultureInfo.InvariantCulture));
                case "isEmpty": Arity(1); return JsonValue.Create(IsEmptyValue(values[0]));
                case "json": Arity(1); return JsonValue.Create(values[0]?.ToJsonString() ?? "null");
                case "parseJson":
                    Arity(1);
                    try { return JsonNode.Parse(Text(0)); }
                    catch (JsonException) { throw new ExpressionException("parseJson() got text that isn't JSON"); }
                case "keys":
                    Arity(1);
                    return values[0] is JsonObject keyed ? new JsonArray(keyed.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray()) : new JsonArray();
                case "first": Arity(1); return values[0] is JsonArray { Count: > 0 } firstOf ? firstOf[0]?.DeepClone() : null;
                case "last": Arity(1); return values[0] is JsonArray { Count: > 0 } lastOf ? lastOf[^1]?.DeepClone() : null;
                default: throw new ExpressionException($"has no function {Name}()");
            }
        }

        private void Arity(int min, int? max = null)
        {
            var most = max ?? min;
            if (Args.Count < min || Args.Count > most)
                throw new ExpressionException(min == most
                    ? $"{Name}() takes {min} argument{(min == 1 ? "" : "s")}"
                    : $"{Name}() takes {min} to {most} arguments");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsEmptyValue(JsonNode? value) => value switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject obj => obj.Count == 0,
        _ => ToText(value).Length == 0
    };

    private static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
        JsonValue scalar when TryNumber(scalar, out var number) => number != 0,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Length > 0 && text != "false",
        _ => true
    };

    private static bool TryNumber(JsonValue value, out decimal number)
    {
        number = 0;
        if (value.GetValueKind() != JsonValueKind.Number) return false;
        return value.TryGetValue(out number) || decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static bool IsNumber(JsonNode? value) => value is JsonValue scalar && TryNumber(scalar, out _);

    // A JSON number, or text that reads as one ("12.5" from a form post).
    private static decimal? Number(JsonNode? value) => value switch
    {
        JsonValue scalar when TryNumber(scalar, out var number) => number,
        JsonValue scalar when scalar.TryGetValue<string>(out var text)
            && decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static decimal RequireNumber(JsonNode? value, string where) =>
        Number(value) ?? throw new ExpressionException($"{where} needs a number, got {(value is null ? "nothing" : $"'{ToText(value)}'")}");

    private static DateTimeOffset RequireDate(JsonNode? value) =>
        DateTimeOffset.TryParse(ToText(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : throw new ExpressionException($"needs a date, got '{ToText(value)}'");

    private static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ExpressionException($"doesn't know the time zone '{id}'");
        }
    }

    private static int Compare(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return (left is null ? 0 : 1) - (right is null ? 0 : 1);
        if (Number(left) is { } a && Number(right) is { } b) return a.CompareTo(b);
        return string.Compare(ToText(left), ToText(right), StringComparison.Ordinal);
    }

    private sealed class ExpressionException(string message) : Exception(message);

    [GeneratedRegex(@"\{\{(?<expr>.+?)\}\}", RegexOptions.Singleline)]
    private static partial Regex TemplatePattern();

    [GeneratedRegex(@"\$json|\$node|[A-Za-z_][A-Za-z0-9_]*\s*\(")]
    private static partial Regex LooksIntended();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugPattern();
}
