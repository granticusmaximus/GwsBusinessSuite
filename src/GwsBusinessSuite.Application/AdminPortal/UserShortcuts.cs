namespace GwsBusinessSuite.Application.AdminPortal;

public sealed record UserShortcutView(string Kind, string Url, string Title, string Icon);

// Pins in the order they were pinned; recents newest first.
public sealed record UserShortcutsView(IReadOnlyList<UserShortcutView> Pins, IReadOnlyList<UserShortcutView> Recents);

public sealed class UserShortcutException(string message) : Exception(message);

// The admin rail's shortcuts (shell redesign R1b): pages pinned from their header, and records
// (an article, a contact, a container...) opened lately.
public interface IUserShortcutService
{
    Task<UserShortcutsView> ListAsync(string username, CancellationToken ct = default);
    Task<bool> IsPinnedAsync(string username, string url, CancellationToken ct = default);
    // Pins the page, or unpins it if it's pinned; returns whether it's pinned afterwards.
    Task<bool> TogglePinAsync(string username, string url, string title, string icon, CancellationToken ct = default);
    Task RecordVisitAsync(string username, string url, string title, string icon, CancellationToken ct = default);
    Task RemoveAsync(string username, string kind, string url, CancellationToken ct = default);
}

public static class UserShortcutRules
{
    public const int MaxPins = 8;
    // How many pins the rail itself shows (the rest are in its Recent list).
    public const int RailPins = 4;
    public const int MaxRecents = 8;

    // "https://admin.example/admin/crm?x=1", "/admin/crm?x=1" and "admin/crm?x=1" are one page;
    // the fragment isn't part of it. Null when there's no usable address.
    public static string? NormalizeUrl(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") text = absolute.PathAndQuery;
        var hash = text.IndexOf('#');
        if (hash >= 0) text = text[..hash];
        text = text.TrimStart('/');
        return text.Length is 0 or > 500 || text.Contains("://", StringComparison.Ordinal) ? null : text;
    }
}
