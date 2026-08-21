using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Web.Services;

/// <summary>
/// Holds the CMS site selected for the current Blazor circuit. The configured Canvas site is
/// only the initial fallback; once selected, every CMS admin screen in the circuit uses the
/// same site until the user changes it.
/// </summary>
public sealed class CurrentCmsSiteAccessor(ICmsBuilderService cmsBuilderService, IConfiguration configuration)
{
    private Guid? _selectedSiteId;

    public async Task<CmsSite> GetCurrentSiteAsync(CancellationToken cancellationToken = default)
    {
        if (_selectedSiteId is { } selectedId)
        {
            var selected = await cmsBuilderService.GetSiteAsync(selectedId, cancellationToken);
            if (selected is not null)
            {
                return selected;
            }
        }

        var slug = configuration["Canvas:SiteSlug"] ?? "grantwatson-dev";
        var site = await cmsBuilderService.GetSiteBySlugAsync(slug, cancellationToken);
        site ??= await cmsBuilderService.SaveSiteAsync(new CmsSiteEditorModel
        {
            Name = configuration["Canvas:SiteName"] ?? slug,
            Slug = slug,
            Theme = "Default"
        }, cancellationToken);

        _selectedSiteId = site.Id;
        return site;
    }

    public async Task<CmsSite?> SelectAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        var site = await cmsBuilderService.GetSiteAsync(siteId, cancellationToken);
        if (site is not null)
        {
            _selectedSiteId = site.Id;
        }

        return site;
    }
}
