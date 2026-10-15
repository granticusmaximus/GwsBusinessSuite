using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// Sentinel item 31: list functions with per-item expressions, let/lets, method-call chaining,
// relation traversal and more date functions. Uses the corpus fixture: row 1 has Tags b, a, b,
// Hours 10, Rate 82.55, Due 31-03-26 14:30 and Client = Acme (tier 1) + Beta (tier 3); row 2 has
// no tags, no due date and no clients.
public sealed class WikiFormulaFeatureTests
{
    private static string Eval(string expression) => WikiFormulaCorpusTests.Run([expression])[0];

    [Theory]
    [InlineData("map([Tags], upper(current))", "\"B, A, B\" | \"\" | ok")]
    [InlineData("map([Tags], index)", "\"0, 1, 2\" | \"\" | ok")]
    [InlineData("length(filter([Tags], current != \"a\"))", "2 | 0 | ok")]
    [InlineData("[Tags].filter(current == \"b\").length()", "2 | 0 | ok")]
    [InlineData("find([Tags], current == \"a\")", "\"a\" | null | ok")]
    [InlineData("some([Tags], current == \"a\")", "true | false | ok")]
    [InlineData("every([Tags], length(current) == 1)", "true | true | ok")]
    [InlineData("[Tags].map(current + index).join(\"-\")", "\"b0-a1-b2\" | \"\" | ok")]
    [InlineData("sum([Hours], [Rate])", "92.55 | 4 | ok")]
    [InlineData("sum(map([Tags], 1))", "3 | 0 | ok")]
    public void ListFunctions_ShouldRunTheirExpressionForEachItem(string expression, string expected) =>
        Eval(expression).Should().Be(expected);

    [Theory]
    [InlineData("let(x, [Hours] * 2, x + 1)", "21 | 5 | ok")]
    [InlineData("lets(a, 1, b, a + 1, a + b)", "3 | 3 | ok")]
    [InlineData("let(limit, 1, [Tags].filter(index >= limit).join())", "\"a, b\" | \"\" | ok")]
    [InlineData("let(current, 5, map([Tags], current))", "\"b, a, b\" | \"\" | ok")]
    public void Let_ShouldBindNames_WithInnerNamesWinning(string expression, string expected) =>
        Eval(expression).Should().Be(expected);

    [Theory]
    [InlineData("prop(\"Hours\")", "10 | 2 | ok")]
    [InlineData("prop(\"Client\").prop(\"Email\")", "\"hello@acme.test, team@beta.test\" | \"\" | ok")]
    [InlineData("[Client].first().prop(\"Name\")", "\"Acme\" | null | ok")]
    [InlineData("[Client].map(current.prop(\"Tier\")).sum()", "4 | 0 | ok")]
    [InlineData("[Client].filter(current.prop(\"Tier\") > 2).prop(\"Name\")", "\"Beta\" | \"\" | ok")]
    [InlineData("[Client].prop(\"Missing\")", "\"#REF! Unknown property [Missing] in Clients\" | \"\" | ok")]
    [InlineData("[Hours].prop(\"Email\")", "\"#ERROR! prop needs a relation to read [Email] from at position 20\" | \"#ERROR! prop needs a relation to read [Email] from at position 20\" | ok")]
    [InlineData("[Notes].upper()", "\"HELLO\" | \"  \" | ok")]
    [InlineData("\"abc\".length()", "3 | 3 | ok")]
    public void Prop_ShouldReadThisRowOrFollowARelation(string expression, string expected) =>
        Eval(expression).Should().Be(expected);

    [Theory]
    [InlineData("year([Due])", "2026 | \"#ERROR! year requires a date value at position 10\" | ok")]
    [InlineData("[Due].month()", "3 | \"#ERROR! month requires a date value at position 12\" | ok")]
    [InlineData("date([Due])", "31 | \"#ERROR! date requires a date value at position 10\" | ok")]
    [InlineData("day([Due])", "2 | \"#ERROR! day requires a date value at position 9\" | ok")]
    [InlineData("week([Due])", "14 | \"#ERROR! week requires a date value at position 10\" | ok")]
    [InlineData("concat(hour([Due]), \":\", minute([Due]))", "\"14:30\" | \"#ERROR! hour requires a date value at position 18\" | ok")]
    [InlineData("formatDate(dateSubtract([Due], 1, \"month\"), \"YYYY-MM-DD\")", "\"2026-02-28\" | \"#ERROR! dateSubtract requires a date value at position 42\" | ok")]
    [InlineData("dateBetween(parseDate(\"2026-12-25\"), parseDate(\"2026-03-31\"), \"days\")", "269 | 269 | ok")]
    [InlineData("parseDate(\"not a date\")", "\"#ERROR! parseDate requires a date value at position 22\" | \"#ERROR! parseDate requires a date value at position 22\" | ok")]
    public void DateFunctions_ShouldReadPartsAndDoArithmetic(string expression, string expected) =>
        Eval(expression).Should().Be(expected);

    [Theory]
    [InlineData("current", "#ERROR! Expected LeftParen at position 6")]
    [InlineData("map([Tags])", "#ERROR! Unknown function or invalid arguments: map at position 10")]
    [InlineData("prop(\"Nope\")", "#REF! Unknown property [Nope]")]
    [InlineData("let(1, 2, 3)", "#ERROR! let needs a name to bind at position 4")]
    [InlineData("[Tags].nope()", "#ERROR! Unknown function or invalid arguments: nope at position 12")]
    [InlineData("[Tags].", "#ERROR! Expected a function name after '.' at position 6")]
    [InlineData("map([Tags], index) + current", "#ERROR! Expected LeftParen at position 27")]
    public void Validation_ShouldRejectMistakesInTheNewSyntax(string expression, string error) =>
        Eval(expression).Should().EndWith($" | {error}");
}
