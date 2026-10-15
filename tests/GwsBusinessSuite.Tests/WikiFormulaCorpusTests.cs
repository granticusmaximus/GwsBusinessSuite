using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

// Sentinel item 31: the formula engine was rebuilt (parse to a tree, then evaluate) to add
// map/filter/let/relation traversal. This corpus pins what every formula that worked before
// evaluated to - on two different rows - and what validation said about it, captured from the
// engine before the rebuild. Any change here is a behavior change for somebody's database.
public sealed class WikiFormulaCorpusTests
{
    // Each entry: expression, then "row 1 | row 2 | validation", as captured from the old engine.
    public static readonly string[] Corpus =
    [
        "1 + 2 * 3", "(1 + 2) * 3", "10 / 4", "10 % 3", "2 ^ 10", "-[Hours]", "[Hours] * [Rate]", "[Hours] / 0",
        "[Empty] + 1", "[Hours] + [Notes]", "\"a\" + 1", "1 + \"2\"", "\"a\" * 2", "2 ^ 0.5", "-(-3)", ".5 + 1", "2 ^ 3 ^ 2", "-2 ^ 2",
        "[Hours] > 8", "[Hours] == 10", "[Notes] == \"hello\"", "\"b\" > \"a\"", "[Due] > \"2026-01-01\"", "[Hours] != [Rate]", "1 < 2 < 3",
        "true and false", "not [Done]", "[Done] or false", "if([Done], \"yes\", \"no\")", "if(true, 1, 1/0)", "and(1, \"x\", true)",
        "or(0, \"\")", "not(\"false\")", "empty([Empty])", "empty([Notes])", "empty([Tags])", "not not true",
        "round(2.5)", "round(-2.5)", "round([Rate], 1)", "abs(-3)", "ceil(1.2)", "floor(-1.2)", "min(3, 1, 2)", "max([Hours], [Rate])",
        "pow(2, 3)", "pow(-1, 0.5)", "min(\"a\")", "round(1.005, 2)",
        "concat([Notes], \"!\", 1, true)", "coalesce([Empty], [Hours])", "coalesce([Empty], [Empty])", "length([Notes])", "lower(\"ABC\")",
        "upper([Notes])", "trim(\"  x  \")", "contains([Notes], \"ELL\")", "startsWith([Notes], \"he\")", "endsWith([Notes], \"lo\")",
        "replace([Notes], \"l\", \"L\")", "'single' + \"double\"",
        "length([Tags])", "first([Tags])", "last([Tags])", "at([Tags], -1)", "at([Tags], 5)", "slice([Tags], 1)", "slice([Tags], 0, -1)",
        "unique([Tags])", "join([Tags])", "join([Tags], \" / \")", "includes([Tags], \"b\")", "sort([Tags])", "[Tags]", "[Client]",
        "length([Client])", "first([Hours])", "join(\"abc\")", "concat([Tags], 1)", "[Tags] == \"b, a\"", "length(slice(sort([Tags]), 1))",
        "[Due]", "dateAdd([Due], 1, \"month\")", "dateAdd([Due], -2, \"days\")", "dateAdd([Due], 1.5, \"weeks\")",
        "dateBetween([Due], dateAdd([Due], -3, \"days\"), \"days\")", "dateBetween([Due], \"2026-01-01\", \"months\")",
        "formatDate([Due], \"YYYY-MM-DD\")", "formatDate([Due], \"DD/MM/YY HH:mm\")", "dateAdd([Due], 1, \"fortnight\")",
        "dateAdd([Notes], 1, \"day\")", "dateBetween(today(), today(), \"days\")", "empty(now())", "dateAdd([Due], 1, \"quarter\") > [Due]",
        "[Status]", "[Name]", "[Total] * 2", "[Loop]", "[Hours] + [Loop]",
        "[Missing]", "foo(1)", "round()", "1 +", "(1", "\"unterminated", "[unterminated", "1 $ 2", "if(1,2)", "true", "TRUE", "and",
        "1,2", "1 2", "[Hours] [Rate]", "IF([Done], 1, 2)", "Round(2.4)", "now", "x"
    ];

    private static readonly DateTimeOffset Created = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static WikiDatabaseProperty Property(Guid databaseId, string name, string type, WikiDatabasePropertyConfiguration? config = null) => new()
    {
        Id = Guid.NewGuid(), WikiDatabaseId = databaseId, Name = name, Type = type, CreatedBy = "u", CreatedAt = Created,
        ConfigJson = config is null ? "{}" : WikiDatabasePropertyConfig.Serialize(config)
    };

    private static WikiDatabasePropertyConfiguration Formula(string expression) => new([], expression, null, null, null, null, null);

    // Two rows with different shapes of data, a related Clients database, and one formula
    // property per expression (plus [Total] and a self-referencing [Loop]).
    public static (WikiDatabase Database, IReadOnlyDictionary<Guid, WikiDatabase> Related, IReadOnlyList<WikiDatabaseProperty> Formulas)
        Build(IReadOnlyList<string> expressions)
    {
        var clients = new WikiDatabase { Id = Guid.NewGuid(), Title = "Clients", CreatedBy = "u", CreatedAt = Created };
        var clientName = Property(clients.Id, "Name", WikiDatabasePropertyTypes.Title);
        var clientEmail = Property(clients.Id, "Email", WikiDatabasePropertyTypes.Text);
        var clientTier = Property(clients.Id, "Tier", WikiDatabasePropertyTypes.Number);
        clients.Properties = [clientName, clientEmail, clientTier];
        var acme = new WikiDatabaseRow { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), WikiDatabaseId = clients.Id, CreatedBy = "u", CreatedAt = Created, SortOrder = 0 };
        var beta = new WikiDatabaseRow { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), WikiDatabaseId = clients.Id, CreatedBy = "u", CreatedAt = Created, SortOrder = 1 };
        void Client(WikiDatabaseRow row, string name, string email, decimal tier)
        {
            var values = new System.Text.Json.Nodes.JsonObject();
            WikiPropertyValues.SetText(values, clientName.Id, name);
            WikiPropertyValues.SetText(values, clientEmail.Id, email);
            WikiPropertyValues.SetNumber(values, clientTier.Id, tier);
            row.PropertyValuesJson = WikiPropertyValues.Serialize(values);
        }
        Client(acme, "Acme", "hello@acme.test", 1);
        Client(beta, "Beta", "team@beta.test", 3);
        clients.Rows = [acme, beta];

        var database = new WikiDatabase { Id = Guid.NewGuid(), Title = "Tasks", CreatedBy = "u", CreatedAt = Created };
        var name = Property(database.Id, "Name", WikiDatabasePropertyTypes.Title);
        var hours = Property(database.Id, "Hours", WikiDatabasePropertyTypes.Number);
        var rate = Property(database.Id, "Rate", WikiDatabasePropertyTypes.Number);
        var done = Property(database.Id, "Done", WikiDatabasePropertyTypes.Checkbox);
        var due = Property(database.Id, "Due", WikiDatabasePropertyTypes.Date);
        var tags = Property(database.Id, "Tags", WikiDatabasePropertyTypes.MultiSelect);
        var notes = Property(database.Id, "Notes", WikiDatabasePropertyTypes.Text);
        var empty = Property(database.Id, "Empty", WikiDatabasePropertyTypes.Number);
        var status = Property(database.Id, "Status", WikiDatabasePropertyTypes.Select);
        var client = Property(database.Id, "Client", WikiDatabasePropertyTypes.Relation, new([], null, clients.Id, null, null, null, null));
        var total = Property(database.Id, "Total", WikiDatabasePropertyTypes.Formula, Formula("[Hours] * [Rate]"));
        var loop = Property(database.Id, "Loop", WikiDatabasePropertyTypes.Formula, Formula("[Loop] + 1"));
        var formulas = expressions.Select((expression, index) => Property(database.Id, $"F{index}", WikiDatabasePropertyTypes.Formula, Formula(expression))).ToList();
        database.Properties = [name, hours, rate, done, due, tags, notes, empty, status, client, total, loop, .. formulas];

        WikiDatabaseRow Row(int order, Action<System.Text.Json.Nodes.JsonObject> fill)
        {
            var values = new System.Text.Json.Nodes.JsonObject();
            fill(values);
            return new WikiDatabaseRow { Id = Guid.NewGuid(), WikiDatabaseId = database.Id, CreatedBy = "u", CreatedAt = Created, SortOrder = order, PropertyValuesJson = WikiPropertyValues.Serialize(values) };
        }
        database.Rows =
        [
            Row(0, values =>
            {
                WikiPropertyValues.SetText(values, name.Id, "Website");
                WikiPropertyValues.SetNumber(values, hours.Id, 10m);
                WikiPropertyValues.SetNumber(values, rate.Id, 82.55m);
                WikiPropertyValues.SetCheckbox(values, done.Id, true);
                WikiPropertyValues.SetDate(values, due.Id, new DateTimeOffset(2026, 3, 31, 14, 30, 0, TimeSpan.Zero));
                WikiPropertyValues.SetMultiSelect(values, tags.Id, ["b", "a", "b"]);
                WikiPropertyValues.SetText(values, notes.Id, "hello");
                WikiPropertyValues.SetMultiSelect(values, client.Id, [acme.Id.ToString(), beta.Id.ToString()]);
            }),
            Row(1, values =>
            {
                WikiPropertyValues.SetNumber(values, hours.Id, 2m);
                WikiPropertyValues.SetNumber(values, rate.Id, 2m);
                WikiPropertyValues.SetText(values, notes.Id, "  ");
            })
        ];
        return (database, new Dictionary<Guid, WikiDatabase> { [clients.Id] = clients }, formulas);
    }

    public static IReadOnlyList<string> Run(IReadOnlyList<string> expressions)
    {
        var (database, related, formulas) = Build(expressions);
        var names = database.Properties.Select(property => property.Name).ToList();
        WikiDatabaseComputation.Materialize(database, related);
        var rows = database.Rows.OrderBy(row => row.SortOrder).Select(row => WikiPropertyValues.ParseObject(row.PropertyValuesJson)).ToList();
        return formulas.Select((formula, index) =>
        {
            string validation;
            try
            {
                WikiDatabaseComputation.ValidateFormula(expressions[index], names);
                validation = "ok";
            }
            catch (Exception exception)
            {
                validation = exception.Message;
            }
            var cells = rows.Select(values => values[formula.Id.ToString()]?.ToJsonString() ?? "null");
            return $"{string.Join(" | ", cells)} | {validation}";
        }).ToList();
    }

    [Fact]
    public void EveryFormulaInTheCorpus_ShouldEvaluateExactlyAsBeforeTheRebuild()
    {
        var actual = Run(Corpus);
        var expected = Golden.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        expected.Should().HaveCount(Corpus.Length);
        for (var index = 0; index < Corpus.Length; index++)
        {
            actual[index].Should().Be(expected[index], $"formula {index}: {Corpus[index]}");
        }
    }

    private const string Golden = """
        7 | 7 | ok
        9 | 9 | ok
        2.5 | 2.5 | ok
        1 | 1 | ok
        1024 | 1024 | ok
        -10 | -2 | ok
        825.50 | 4 | ok
        "#DIV/0!" | "#DIV/0!" | #DIV/0!
        "1" | "1" | ok
        "10hello" | "2  " | ok
        "a1" | "a1" | ok
        3 | 3 | ok
        "#ERROR! This operator requires numeric values at position 6" | "#ERROR! This operator requires numeric values at position 6" | #ERROR! This operator requires numeric values at position 6
        1.4142135623731 | 1.4142135623731 | ok
        3 | 3 | ok
        1.5 | 1.5 | ok
        512 | 512 | ok
        -4 | -4 | ok
        true | false | ok
        true | false | ok
        true | false | ok
        true | true | ok
        true | false | ok
        true | false | ok
        false | false | ok
        false | false | ok
        false | true | ok
        true | false | ok
        "yes" | "no" | ok
        "#DIV/0!" | "#DIV/0!" | #DIV/0!
        true | true | ok
        false | false | ok
        true | true | ok
        true | true | ok
        false | true | ok
        false | true | ok
        true | true | ok
        3 | 3 | ok
        -3 | -3 | ok
        82.6 | 2 | ok
        3 | 3 | ok
        2 | 2 | ok
        -2 | -2 | ok
        1 | 1 | ok
        82.55 | 2 | ok
        8 | 8 | ok
        "#ERROR! Power result is outside the supported range at position 11" | "#ERROR! Power result is outside the supported range at position 11" | ok
        "#ERROR! min requires numeric values at position 7" | "#ERROR! min requires numeric values at position 7" | ok
        1.01 | 1.01 | ok
        "hello!1True" | "  !1True" | ok
        10 | 2 | ok
        null | null | ok
        5 | 2 | ok
        "abc" | "abc" | ok
        "HELLO" | "  " | ok
        "x" | "x" | ok
        true | false | ok
        true | false | ok
        true | false | ok
        "heLLo" | "  " | ok
        "singledouble" | "singledouble" | ok
        3 | 0 | ok
        "b" | null | ok
        "b" | null | ok
        "b" | null | ok
        null | null | ok
        "a, b" | "" | ok
        "b, a" | "" | ok
        "b, a" | "" | ok
        "b, a, b" | "" | ok
        "b / a / b" | "" | ok
        true | false | ok
        "a, b, b" | "" | ok
        "b, a, b" | "" | ok
        "aaaaaaaa-0000-0000-0000-000000000001, aaaaaaaa-0000-0000-0000-000000000002" | "" | ok
        2 | 0 | ok
        10 | 2 | ok
        "abc" | "abc" | ok
        "b, a, b1" | "1" | ok
        false | false | ok
        2 | 0 | ok
        "2026-03-31T14:30:00.0000000\u002B00:00" | null | ok
        "2026-04-30T14:30:00.0000000\u002B00:00" | "#ERROR! dateAdd requires a date value at position 25" | ok
        "2026-03-29T14:30:00.0000000\u002B00:00" | "#ERROR! dateAdd requires a date value at position 25" | ok
        "2026-04-11T02:30:00.0000000\u002B00:00" | "#ERROR! dateAdd requires a date value at position 27" | ok
        3 | "#ERROR! dateAdd requires a date value at position 45" | ok
        2 | "#ERROR! dateBetween requires a date value at position 41" | ok
        "2026-03-31" | "#ERROR! formatDate requires a date value at position 30" | ok
        "31/03/26 14:30" | "#ERROR! formatDate requires a date value at position 34" | ok
        "#ERROR! Unsupported date unit: fortnight at position 29" | "#ERROR! dateAdd requires a date value at position 29" | ok
        "#ERROR! dateAdd requires a date value at position 25" | "#ERROR! dateAdd requires a date value at position 25" | ok
        0 | 0 | ok
        false | false | ok
        true | "#ERROR! dateAdd requires a date value at position 29" | ok
        "" | "" | ok
        "Website" | "" | ok
        1651.00 | 8 | ok
        "#CYCLE!1" | "#CYCLE!1" | ok
        "10#CYCLE!1" | "2#CYCLE!1" | ok
        "#REF! Unknown property [Missing]" | "#REF! Unknown property [Missing]" | #REF! Unknown property [Missing]
        "#ERROR! Unknown function or invalid arguments: foo at position 5" | "#ERROR! Unknown function or invalid arguments: foo at position 5" | #ERROR! Unknown function or invalid arguments: foo at position 5
        "#ERROR! Unknown function or invalid arguments: round at position 6" | "#ERROR! Unknown function or invalid arguments: round at position 6" | #ERROR! Unknown function or invalid arguments: round at position 6
        "#ERROR! Expected a number, string, property, function, or parenthesized expression at position 2" | "#ERROR! Expected a number, string, property, function, or parenthesized expression at position 2" | #ERROR! Expected a number, string, property, function, or parenthesized expression at position 2
        "#ERROR! Expected RightParen at position 1" | "#ERROR! Expected RightParen at position 1" | #ERROR! Expected RightParen at position 1
        "#ERROR! Unterminated string at position 12" | "#ERROR! Unterminated string at position 12" | #ERROR! Unterminated string at position 12
        "#ERROR! Unterminated property reference at position 12" | "#ERROR! Unterminated property reference at position 12" | #ERROR! Unterminated property reference at position 12
        "#ERROR! Unexpected character \u0027$\u0027 at position 2" | "#ERROR! Unexpected character \u0027$\u0027 at position 2" | #ERROR! Unexpected character '$' at position 2
        "#ERROR! Unknown function or invalid arguments: if at position 6" | "#ERROR! Unknown function or invalid arguments: if at position 6" | #ERROR! Unknown function or invalid arguments: if at position 6
        true | true | ok
        true | true | ok
        "#ERROR! Expected LeftParen at position 2" | "#ERROR! Expected LeftParen at position 2" | #ERROR! Expected LeftParen at position 2
        "#ERROR! Unexpected token \u0027,\u0027 at position 1" | "#ERROR! Unexpected token \u0027,\u0027 at position 1" | #ERROR! Unexpected token ',' at position 1
        "#ERROR! Unexpected token \u00272\u0027 at position 2" | "#ERROR! Unexpected token \u00272\u0027 at position 2" | #ERROR! Unexpected token '2' at position 2
        "#ERROR! Unexpected token \u0027Rate\u0027 at position 13" | "#ERROR! Unexpected token \u0027Rate\u0027 at position 13" | #ERROR! Unexpected token 'Rate' at position 13
        1 | 2 | ok
        2 | 2 | ok
        "#ERROR! Expected LeftParen at position 2" | "#ERROR! Expected LeftParen at position 2" | #ERROR! Expected LeftParen at position 2
        "#ERROR! Expected LeftParen at position 0" | "#ERROR! Expected LeftParen at position 0" | #ERROR! Expected LeftParen at position 0
        """;
}
