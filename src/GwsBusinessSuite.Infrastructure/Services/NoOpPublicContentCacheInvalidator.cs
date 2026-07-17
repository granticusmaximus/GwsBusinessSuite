using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NoOpPublicContentCacheInvalidator : IPublicContentCacheInvalidator
{
    public ValueTask InvalidateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
