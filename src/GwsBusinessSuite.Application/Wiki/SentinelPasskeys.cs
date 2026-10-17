namespace GwsBusinessSuite.Application.Wiki;

// Page lock, Touch ID in the browser (phase 2): WebAuthn passkeys on the platform authenticator -
// Touch ID on a Mac in Safari/Chrome/Edge, Face ID/Touch ID on an iPhone. User verification is
// required, so a passkey unlock always means the fingerprint (or the device passcode) was given.
// Options and responses travel as WebAuthn JSON between the browser (sentinel-passkey.js) and
// the server; the server keeps each ceremony's options for 5 minutes and checks them on return.
public sealed record SentinelPasskeyView(Guid Id, string Name, long CreatedAtUnix, long? LastUsedAtUnix);

public interface ISentinelPasskeyService
{
    // origin: the page's own origin (https://admin.gwsapp.net, or localhost in development) - it
    // must be one Sentinel accepts, and becomes the passkey's relying party.
    // Adding a passkey when another unlock method exists needs a live unlock in this session.
    Task<string> BeginRegistrationAsync(string username, string? sessionId, string origin, CancellationToken cancellationToken = default);
    Task<SentinelPasskeyView> CompleteRegistrationAsync(string username, string origin, string name, string attestationJson, CancellationToken cancellationToken = default);
    Task<string> BeginUnlockAsync(string username, string? sessionId, string origin, CancellationToken cancellationToken = default);
    // Verifies the Touch ID assertion and unlocks this person's pages in this session; returns
    // the unlock's expiry (Unix seconds).
    Task<long> CompleteUnlockAsync(string username, string? sessionId, string origin, string assertionJson, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelPasskeyView>> ListAsync(string username, CancellationToken cancellationToken = default);
    // "Sign in with Touch ID" on the login page: a discoverable-passkey ceremony with no username
    // typed. Returns the passkey owner's username once the assertion is verified.
    Task<(string CeremonyId, string OptionsJson)> BeginSignInAsync(string origin, CancellationToken cancellationToken = default);
    Task<string> CompleteSignInAsync(string ceremonyId, string origin, string assertionJson, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid passkeyId, string username, CancellationToken cancellationToken = default);
}
