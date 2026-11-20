using System.Globalization;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Tests;

// 19-11-26: three deploys failed because "C0" and "P0" print "¤1,200" and "88 %" in the invariant
// culture CI and the container run with, while a Mac prints "$1,200" and "88%". GwsMoney is the same
// everywhere.
public sealed class GwsMoneyTests
{
    [Fact]
    public void Money_ShouldNotDependOnTheCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("de-DE") })
            {
                CultureInfo.CurrentCulture = culture;
                GwsMoney.Usd(1200m).Should().Be("$1,200", culture.Name);
                GwsMoney.Usd(-50m).Should().Be("-$50");
                GwsMoney.Percent(0.875).Should().Be("88%");
            }
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
}
