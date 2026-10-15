// Public Sentinel shares (SentinelPublicShare.razor) are a static page, so nothing calls
// wikiRichContentHydrate from .NET: draw code, equation and diagram blocks here, on the first
// load and again after Blazor's enhanced navigation swaps in another page of a shared site.
(function () {
    function hydrate() {
        if (window.wikiRichContentHydrate && document.querySelector('.sentinel-public-content')) {
            window.wikiRichContentHydrate('.sentinel-public-content');
        }
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', hydrate);
    else hydrate();
    document.addEventListener('blazor:enhancedload', hydrate);
    window.addEventListener('load', function () {
        if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
            window.Blazor.addEventListener('enhancedload', hydrate);
        }
    });
})();
