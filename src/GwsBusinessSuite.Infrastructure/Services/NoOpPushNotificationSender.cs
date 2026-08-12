using GwsBusinessSuite.Application.Mobile;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// The only IPushNotificationSender implementation available in this environment - no APNs/FCM
// credentials or the separate MAUI client project exist here (Part 4.8 is server-side readiness
// only). Logging instead of silently no-op-ing keeps that gap visible in production logs rather
// than looking like delivery quietly succeeded.
public sealed class NoOpPushNotificationSender(ILogger<NoOpPushNotificationSender> logger) : IPushNotificationSender
{
    public Task SendAsync(string username, string title, string body, string? deepLinkUrl = null, CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "Push notification to {Username} not delivered - no push provider is configured (APNs/FCM credentials not set up). Title: {Title}",
            username, title);
        return Task.CompletedTask;
    }
}
