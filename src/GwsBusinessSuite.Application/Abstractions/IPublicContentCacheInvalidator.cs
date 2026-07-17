namespace GwsBusinessSuite.Application.Abstractions;

public interface IPublicContentCacheInvalidator
{
    ValueTask InvalidateAsync(CancellationToken cancellationToken = default);
}
