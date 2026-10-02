using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Web.Services;

/// <summary>
/// Supplies the admin portal's theme CSS (see <see cref="AdminThemeCss"/>) from the live
/// website's design tokens - the site configured as Canvas:SiteSlug, the same one the public
/// host serves - so applying a theme to the website re-colors the admin as well. Cached because
/// every admin page load asks for it; Customize calls <see cref="Invalidate"/> after a change so
/// the new colors show on the very next load.
/// </summary>
public sealed class AdminThemeProvider(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IMemoryCache cache,
    ILogger<AdminThemeProvider> logger)
{
    private const string CacheKey = "admin-theme-css";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public async Task<string> GetCssAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out string? cached) && cached is not null)
        {
            return cached;
        }

        var css = string.Empty;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var cms = scope.ServiceProvider.GetRequiredService<ICmsBuilderService>();
            var slug = configuration["Canvas:SiteSlug"] ?? "grantwatson-dev";
            var site = await cms.GetSiteBySlugAsync(slug, cancellationToken);
            css = AdminThemeCss.Build(DesignTokenJson.ParseOrEmpty(site?.DesignTokensJson));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The admin must always render - fall back to its built-in colors.
            logger.LogWarning(ex, "Unable to load the website theme for the admin portal; using default admin colors.");
        }

        cache.Set(CacheKey, css, CacheDuration);
        return css;
    }

    public void Invalidate() => cache.Remove(CacheKey);
}
