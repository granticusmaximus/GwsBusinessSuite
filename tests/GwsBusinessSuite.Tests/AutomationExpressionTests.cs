using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GwsBusinessSuite.Tests;

// The {{ }} expression language, and the sandboxed Code step.
public sealed class AutomationExpressionTests
{
    private static readonly JsonElement Input = JsonDocument.Parse("""
        {"name":"ada lovelace","price":"12.50","qty":3,"total":1.50,"enabled":true,"content-type":"json",
         "items":[{"sku":"A1"},{"sku":"B2"}],"tags":["x","y"],"empty":"","when":"2026-10-19T13:05:00Z","customer":{"email":"ada@example.com"}}
        """).RootElement;

    private static readonly IReadOnlyDictionary<string, JsonElement> Nodes = new Dictionary<string, JsonElement>
    {
        ["Fetch"] = JsonDocument.Parse("""{"status":200,"body":{"id":"42"}}""").RootElement
    };

    private static string R(string template) => AutomationExpressions.Render(template, Input, Nodes);

    [Theory]
    [InlineData("Hi {{ $json.name }}!", "Hi ada lovelace!")]
    [InlineData("{{$json.customer.email}}", "ada@example.com")]
    [InlineData("{{ $json.total }}", "1.50")]
    [InlineData("{{ $json.missing.deep }}", "")]
    [InlineData("{{ $json.customer }}", """{"email":"ada@example.com"}""")]
    [InlineData("{{ $json.content-type }}", "json")]
    [InlineData("{{ $node(\"Fetch\").json.body.id }}", "42")]
    [InlineData("{{ $node('Nope').json.x }}", "")]
    public void PlainPaths_ShouldWorkExactlyAsBefore(string template, string expected) => R(template).Should().Be(expected);

    [Theory]
    [InlineData("{{ $json.items[1].sku }}", "B2")]
    [InlineData("{{ $json.items[-1].sku }}", "B2")]
    [InlineData("{{ $json.items.0.sku }}", "A1")]
    [InlineData("{{ upper($json.name) }}", "ADA LOVELACE")]
    [InlineData("{{ default($json.empty, 'n/a') }}", "n/a")]
    [InlineData("{{ coalesce($json.missing, $json.empty, 'third') }}", "third")]
    [InlineData("{{ $json.price * $json.qty }}", "37.50")]
    [InlineData("{{ round($json.price / $json.qty, 2) }}", "4.17")]
    [InlineData("{{ $json.qty + 1 }}", "4")]
    [InlineData("{{ 'Order ' + $json.qty }}", "Order 3")]
    [InlineData("{{ if($json.qty > 2 && $json.enabled, 'bulk', 'single') }}", "bulk")]
    [InlineData("{{ !$json.enabled }}", "false")]
    [InlineData("{{ join($json.tags, ' / ') }}", "x / y")]
    [InlineData("{{ length($json.items) }}", "2")]
    [InlineData("{{ slugify('Hello, World!') }}", "hello-world")]
    [InlineData("{{ replace($json.name, 'ada', 'Ada') }}", "Ada lovelace")]
    [InlineData("{{ substring($json.name, 4) }}", "lovelace")]
    [InlineData("{{ contains($json.tags, 'y') }}", "true")]
    [InlineData("{{ split('a,b', ',')[1] }}", "b")]
    [InlineData("{{ formatDate($json.when, 'dd-MM-yy HH:mm', 'America/New_York') }}", "19-10-26 09:05")]
    [InlineData("{{ formatDate(addDays($json.when, 1), 'dd-MM-yy', 'UTC') }}", "20-10-26")]
    [InlineData("{{ keys($json.customer) }}", """["email"]""")]
    [InlineData("{{ parseJson('{\"a\":1}').a }}", "1")]
    [InlineData("{{ max(3, $json.qty, 7) }}", "7")]
    public void Expressions_ShouldEvaluate(string template, string expected) => R(template).Should().Be(expected);

    [Theory]
    [InlineData("Hello {{name}}")]
    [InlineData("{{ #each items }}")]
    [InlineData("{{ upper($json.name }}")]
    public void TextThatIsntAnExpression_ShouldBeLeftAlone(string template) => R(template).Should().Be(template);

    [Fact]
    public void RuntimeErrors_ShouldSayWhatWentWrong()
    {
        var act = () => R("{{ $json.qty / 0 }}");
        act.Should().Throw<InvalidOperationException>().WithMessage("*divides by zero*");
        ((Action)(() => R("{{ round($json.name) }}"))).Should().Throw<InvalidOperationException>().WithMessage("*round needs a number*");
        ((Action)(() => R("{{ upper() }}"))).Should().Throw<InvalidOperationException>().WithMessage("*upper() takes 1 argument*");
    }

    [Fact]
    public void FindErrors_ShouldFlagBrokenExpressions_ButNotLiteralTemplates()
    {
        AutomationExpressions.FindErrors("Hi {{name}} and {{ $json.ok }}").Should().BeEmpty();
        AutomationExpressions.FindErrors("{{ upper($json.name }}").Should().ContainSingle().Which.Should().Contain("missing ')'");
        AutomationExpressions.FindErrors("{{ shout($json.name) }}").Should().ContainSingle().Which.Should().Contain("no function shout()");
        AutomationExpressions.FindErrors("{{ $jsn.name }}").Should().BeEmpty("not recognisably an expression");
    }

    [Fact]
    public void DeeplyNestedOrHugeExpressions_ShouldBeRefused()
    {
        var deep = "{{ " + new string('(', 200) + "1" + new string(')', 200) + " }}";
        AutomationExpressions.FindErrors(deep.Replace("{{ ", "{{ upper(").Replace(" }}", ") }}")).Should().ContainSingle().Which.Should().Contain("nested too deeply");
        R(deep).Should().Be(deep, "an expression that can't be parsed is left as written");
    }

    // ---- Code step ----

    private static readonly JintAutomationCodeRunner Runner = new();

    private static async Task<List<string>> Run(string code) =>
        (await Runner.RunAsync(code, Input, Nodes)).Select(item => item.GetRawText()).ToList();

    [Fact]
    public async Task Code_ShouldTransformTheItem_AndSplitArrays()
    {
        (await Run("return { name: $json.name.toUpperCase(), total: Number($json.price) * $json.qty, id: $node('Fetch').body.id };"))
            .Should().Equal("""{"name":"ADA LOVELACE","total":37.5,"id":"42"}""");
        (await Run("return $json.items.map(i => ({ sku: i.sku }));")).Should().Equal("""{"sku":"A1"}""", """{"sku":"B2"}""");
        (await Run("return 7;")).Should().Equal("""{"result":7}""");
        (await Run("const x = 1;")).Single().Should().Contain("ada lovelace", "returning nothing passes the input on");
        (await Run("console.log('hi', { a: 1 }); return { ok: true };")).Should().Equal("""{"ok":true,"_logs":["hi {\"a\":1}"]}""");
    }

    [Theory]
    [InlineData("while (true) {}", "*too many steps*")]
    [InlineData("const a = []; while (true) a.push('x'.repeat(1000));", "*memory*")]
    [InlineData("const f = () => f(); return f();", "*recursed too deeply*")]
    [InlineData("throw new Error('bad order');", "*Code step error: bad order (line 1)*")]
    [InlineData("return {", "Code step has a syntax error: *(at the end of your code)")]
    [InlineData("const x = ;\nreturn x;", "Code step has a syntax error: * (line 1)")]
    [InlineData("return typeof System + typeof require + typeof fetch + typeof setTimeout + typeof importNamespace;", "")]
    public async Task Code_ShouldBeSandboxed(string code, string error)
    {
        if (error.Length == 0)
        {
            (await Run(code)).Single().Should().Be("""{"result":"undefinedundefinedundefinedundefinedundefined"}""", "no .NET, modules, network or timers");
            return;
        }
        var act = () => Run(code);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(error);
    }

    [Fact]
    public async Task Code_ShouldRefuseEmptyOrHugeCode_AndTooManyItems()
    {
        await ((Func<Task>)(() => Run("  "))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*no code*");
        await ((Func<Task>)(() => Run(new string('/', AutomationCodeStep.MaxCodeLength + 1)))).Should().ThrowAsync<InvalidOperationException>();
        await ((Func<Task>)(() => Run("return Array.from({ length: 1001 }, (_, i) => ({ i }));"))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*more than 1,000 items*");
    }

    [Fact]
    public async Task CodeNode_ShouldRunThroughTheRegistry_AndSplitIntoItems()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAutomationCodeRunner, JintAutomationCodeRunner>()
            .BuildServiceProvider();
        var registry = new AutomationNodeRegistry(new NoHttp(), serviceProvider: services);
        var node = new AutomationNodeSnapshot(Guid.NewGuid(), "Split", AutomationCodeStep.TypeKey, 1,
            """{"code":"return $json.items.map(i => ({ sku: i.sku, from: $node('Fetch').status }));"}""",
            null, false, false, false, 1, 0, 0);

        var result = await registry.ExecuteAsync(node, Input, null, nodeOutputsByName: Nodes);

        result.Outputs["main"].Select(item => item.GetRawText()).Should().Equal("""{"sku":"A1","from":200}""", """{"sku":"B2","from":200}""");
        registry.Find(AutomationCodeStep.TypeKey, 1)!.DisplayName.Should().Be("Code");

        var withoutRunner = new AutomationNodeRegistry(new NoHttp());
        await withoutRunner.Invoking(r => r.ExecuteAsync(node, Input, null)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*isn't available*");
    }

    private sealed class NoHttp : IAutomationHttpClient
    {
        public Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
