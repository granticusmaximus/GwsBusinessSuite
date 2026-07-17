using GwsBusinessSuite.Application.Abstractions;
using Microsoft.AspNetCore.OutputCaching;

namespace GwsBusinessSuite.Web.Services;

public sealed class OutputCachePublicContentInvalidator(
    IOutputCacheStore outputCache,
    ILogger<OutputCachePublicContentInvalidator> logger) : IPublicContentCacheInvalidator
{
    public const string Tag = "public-content";

    public async ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
    {
        await outputCache.EvictByTagAsync(Tag, cancellationToken);
        logger.LogInformation("Invalidated anonymous public-content output cache");
    }
}
