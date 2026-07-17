using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Settings;

public sealed class SiteSettingsService(
    IAppDbContext db,
    ICurrentUserAccessor? currentUserAccessor = null) : ISiteSettingsService
{
    private readonly ICurrentUserAccessor _currentUserAccessor = currentUserAccessor ?? FixedCurrentUserAccessor.Unknown;

    // No row yet just means "nothing has been customized" - return the same defaults the
    // SiteSettings entity itself declares, without writing a row on what's otherwise a
    // read-only hot path (this is queried on every /blog request, media upload, etc.).
    public async Task<SiteSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var defaults = new SiteSettings();

        return new SiteSettingsView(
            row?.PostsPerPage ?? defaults.PostsPerPage,
            row?.DefaultArticleCategoryId,
            row?.DefaultAuthorByline,
            row?.OllamaModelOverride,
            row?.OllamaTimeoutMinutesOverride,
            row?.HeroImageModelOverride,
            row?.MaxMediaUploadSizeMb ?? defaults.MaxMediaUploadSizeMb);
    }

    public async Task SaveSettingsAsync(SiteSettingsView settings, CancellationToken cancellationToken = default)
    {
        var row = await db.SiteSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new SiteSettings();
            db.SiteSettings.Add(row);
        }

        row.PostsPerPage = settings.PostsPerPage is 10 or 12 or 25 or 50 ? settings.PostsPerPage : 12;
        row.DefaultArticleCategoryId = settings.DefaultArticleCategoryId;
        row.DefaultAuthorByline = string.IsNullOrWhiteSpace(settings.DefaultAuthorByline) ? null : settings.DefaultAuthorByline.Trim();
        row.OllamaModelOverride = string.IsNullOrWhiteSpace(settings.OllamaModelOverride) ? null : settings.OllamaModelOverride.Trim();
        // Upper bounds mirror Settings.razor's <input min/max> - those are client-side
        // only (HTML attributes don't stop a direct API/service call), so the real
        // enforcement has to live here too. 180 min matches the UI's generation-timeout
        // cap; 100 MB matches its media-upload cap (MediaLibraryService's own hard
        // ceiling on individual uploads).
        row.OllamaTimeoutMinutesOverride = settings.OllamaTimeoutMinutesOverride is > 0
            ? Math.Min(settings.OllamaTimeoutMinutesOverride.Value, 180)
            : null;
        row.HeroImageModelOverride = string.IsNullOrWhiteSpace(settings.HeroImageModelOverride) ? null : settings.HeroImageModelOverride.Trim();
        row.MaxMediaUploadSizeMb = settings.MaxMediaUploadSizeMb > 0 ? Math.Min(settings.MaxMediaUploadSizeMb, 100) : 8;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }
}
