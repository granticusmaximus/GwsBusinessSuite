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

        row.PostsPerPage = settings.PostsPerPage is 10 or 25 or 50 ? settings.PostsPerPage : 10;
        row.DefaultArticleCategoryId = settings.DefaultArticleCategoryId;
        row.DefaultAuthorByline = string.IsNullOrWhiteSpace(settings.DefaultAuthorByline) ? null : settings.DefaultAuthorByline.Trim();
        row.OllamaModelOverride = string.IsNullOrWhiteSpace(settings.OllamaModelOverride) ? null : settings.OllamaModelOverride.Trim();
        row.OllamaTimeoutMinutesOverride = settings.OllamaTimeoutMinutesOverride is > 0 ? settings.OllamaTimeoutMinutesOverride : null;
        row.MaxMediaUploadSizeMb = settings.MaxMediaUploadSizeMb > 0 ? settings.MaxMediaUploadSizeMb : 8;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }
}
