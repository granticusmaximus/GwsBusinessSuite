using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Page lock phase 2: real WebAuthn ceremonies - the browser module (sentinel-passkey.js) against
// the server's verification (SentinelPasskeyService), with Chromium's virtual authenticator
// standing in for Touch ID (user verification on).
[Collection("Playwright")]
public sealed class SentinelPasskeyBrowserTests(PlaywrightBrowserFixture fixture)
{
    private const string Origin = "http://localhost";
    private const string Session = "session-a";
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    [Fact]
    public async Task Passkey_ShouldRegister_UnlockLockedPages_AndRefuseAReplay()
    {
        var (db, passkeys, locks) = await CreateAsync();
        await using var page = await OpenAsync();

        var registerOptions = await passkeys.BeginRegistrationAsync("grant", Session, Origin);
        var attestation = await page.EvaluateAsync<string>("o => window.passkey.create(o)", registerOptions);
        var registered = await passkeys.CompleteRegistrationAsync("grant", Origin, "MacBook Touch ID", attestation);

        registered.Name.Should().Be("MacBook Touch ID");
        (await locks.GetMethodsAsync("grant")).Passkeys.Should().Be(1);

        var unlockOptions = await passkeys.BeginUnlockAsync("grant", Session, Origin);
        var assertion = await page.EvaluateAsync<string>("o => window.passkey.get(o)", unlockOptions);
        var expires = await passkeys.CompleteUnlockAsync("grant", Session, Origin, assertion);

        expires.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        (await locks.IsUnlockedAsync("grant", Session)).Should().BeTrue();
        (await db.SentinelPasskeys.AsNoTracking().SingleAsync()).LastUsedAtUnix.Should().NotBeNull();

        // The same answer again (a replay) has no challenge left to match.
        await locks.LockAllNowAsync("grant");
        await passkeys.Invoking(p => p.CompleteUnlockAsync("grant", Session, Origin, assertion)).Should().ThrowAsync<SentinelLockException>();
        // A fresh challenge answered for a different challenge fails verification.
        await passkeys.BeginUnlockAsync("grant", Session, Origin);
        await passkeys.Invoking(p => p.CompleteUnlockAsync("grant", Session, Origin, assertion)).Should().ThrowAsync<SentinelLockException>();
        (await locks.IsUnlockedAsync("grant", Session)).Should().BeFalse();
    }

    [Fact]
    public async Task Passkey_ShouldOnlyWorkForItsOwner_AndOnSentinelsOwnHosts()
    {
        var (_, passkeys, _) = await CreateAsync();
        await using var page = await OpenAsync();
        var attestation = await page.EvaluateAsync<string>("o => window.passkey.create(o)", await passkeys.BeginRegistrationAsync("grant", Session, Origin));
        await passkeys.CompleteRegistrationAsync("grant", Origin, "Touch ID", attestation);

        await passkeys.Invoking(p => p.BeginRegistrationAsync("grant", Session, "https://evil.example")).Should().ThrowAsync<SentinelLockException>();
        await passkeys.Invoking(p => p.BeginSignInAsync("http://grantwatson.dev")).Should().ThrowAsync<SentinelLockException>();
        // The admin also runs on the public site's hosts; www shares the bare domain's passkeys.
        (await passkeys.BeginSignInAsync("https://admin.gwsapp.net")).OptionsJson.Should().Contain("\"rpId\":\"admin.gwsapp.net\"");
        (await passkeys.BeginSignInAsync("https://grantwatson.dev")).OptionsJson.Should().Contain("\"rpId\":\"grantwatson.dev\"");
        (await passkeys.BeginSignInAsync("https://www.grantwatson.dev")).OptionsJson.Should().Contain("\"rpId\":\"grantwatson.dev\"");
        await passkeys.Invoking(p => p.BeginUnlockAsync("sam", Session, Origin)).Should().ThrowAsync<SentinelLockException>().WithMessage("Add a Touch ID*");

        // Grant's passkey answering Sam's unlock: not registered for Sam.
        var grantOptions = await passkeys.BeginUnlockAsync("grant", Session, Origin);
        var assertion = await page.EvaluateAsync<string>("o => window.passkey.get(o)", grantOptions);
        await passkeys.Invoking(p => p.CompleteUnlockAsync("sam", Session, Origin, assertion)).Should().ThrowAsync<SentinelLockException>();
    }

    [Fact]
    public async Task SignIn_ShouldFindThePasskeysOwner_WithoutAUsername()
    {
        var (_, passkeys, locks) = await CreateAsync();
        await using var page = await OpenAsync();
        var attestation = await page.EvaluateAsync<string>("o => window.passkey.create(o)", await passkeys.BeginRegistrationAsync("grant", Session, Origin));
        await passkeys.CompleteRegistrationAsync("grant", Origin, "Touch ID", attestation);

        var (ceremonyId, options) = await passkeys.BeginSignInAsync(Origin);
        var assertion = await page.EvaluateAsync<string>("o => window.passkey.get(o)", options);

        (await passkeys.CompleteSignInAsync(ceremonyId, Origin, assertion)).Should().Be("grant");
        // The ceremony is used up; a made-up one never existed.
        await passkeys.Invoking(p => p.CompleteSignInAsync(ceremonyId, Origin, assertion)).Should().ThrowAsync<SentinelLockException>();
        await passkeys.Invoking(p => p.CompleteSignInAsync("made-up", Origin, assertion)).Should().ThrowAsync<SentinelLockException>();
        // A second passkey needs an unlock first (a stolen session can't add one).
        await locks.SetPinAsync("sam", Session, null, "112233");
        await passkeys.Invoking(p => p.BeginRegistrationAsync("grant", "other-session", Origin)).Should().ThrowAsync<SentinelLockException>().WithMessage("Unlock first*");
    }

    private async Task<IPage> OpenAsync()
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html", Body = "<!DOCTYPE html><html><body>passkey harness</body></html>"
        }));
        await page.RouteAsync("http://localhost/js/sentinel-passkey.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Path = Path.Combine(WebRoot, "js", "sentinel-passkey.js")
        }));
        await page.GotoAsync("http://localhost/harness");
        var cdp = await page.Context.NewCDPSessionAsync(page);
        await cdp.SendAsync("WebAuthn.enable");
        await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["protocol"] = "ctap2", ["transport"] = "internal", ["hasResidentKey"] = true,
                ["hasUserVerification"] = true, ["isUserVerified"] = true, ["automaticPresenceSimulation"] = true
            }
        });
        await page.EvaluateAsync("async () => { window.passkey = await import('/js/sentinel-passkey.js'); }");
        (await page.EvaluateAsync<bool>("() => window.passkey.isAvailable()")).Should().BeTrue();
        return page;
    }

    private static async Task<(ApplicationDbContext Db, SentinelPasskeyService Passkeys, SentinelPageLockService Locks)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        var locks = new SentinelPageLockService(db, new SentinelAccessService(db),
            new DataProtectionPageLockCrypto(new EphemeralDataProtectionProvider()), TimeProvider.System);
        var passkeys = new SentinelPasskeyService(db, locks, new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().Build(), TimeProvider.System);
        return (db, passkeys, locks);
    }
}
