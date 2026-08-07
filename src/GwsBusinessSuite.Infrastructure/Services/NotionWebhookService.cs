using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NotionWebhookService(
    IAppDbContext dbContext,
    ISecretProtector secretProtector,
    INotionSyncCoordinator syncCoordinator,
    ILogger<NotionWebhookService> logger) : INotionWebhookService
{
    public async Task<NotionWebhookHandleResult> HandleAsync(
        string rawBody,
        string? signature,
        CancellationToken cancellationToken = default)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return new(400, "Webhook payload must be valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("verification_token", out var verificationElement)
                && verificationElement.ValueKind == JsonValueKind.String
                && verificationElement.GetString() is { Length: > 0 } verificationToken)
            {
                var settings = await dbContext.NotionConnectorSettings
                    .FirstOrDefaultAsync(cancellationToken);
                if (settings is null || string.IsNullOrWhiteSpace(settings.IntegrationToken))
                {
                    return new(409, "Connect a Notion workspace before verifying its webhook.");
                }

                // Verification is one-time. The read-then-write shape this used to have (check
                // WebhookVerificationToken is empty, then SaveChangesAsync separately) had a
                // real race window: two concurrent anonymous requests each carrying a different
                // verification_token could both pass the empty check before either committed,
                // so whichever's SaveChangesAsync landed last would win - an anonymous caller
                // racing the real Notion verification click could poison the signing secret
                // with their own token instead of Notion's. ExecuteUpdateAsync compiles to a
                // single atomic `UPDATE ... WHERE WebhookVerificationToken IS NULL OR = ''`
                // statement; the database itself serializes the two requests, and only the one
                // whose WHERE clause still matches at commit time actually writes anything.
                var now = DateTimeOffset.UtcNow;
                var protectedToken = secretProtector.Protect(verificationToken);
                var rowsUpdated = await dbContext.NotionConnectorSettings
                    .Where(item => item.Id == settings.Id
                        && (item.WebhookVerificationToken == null || item.WebhookVerificationToken == string.Empty))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.WebhookVerificationToken, protectedToken)
                        .SetProperty(item => item.WebhookVerificationReceivedAt, now)
                        .SetProperty(item => item.UpdatedAt, now)
                        .SetProperty(item => item.UpdatedBy, "notion-webhook"),
                        cancellationToken);
                if (rowsUpdated == 0)
                {
                    return new(409, "The Notion webhook is already verified.");
                }

                // ExecuteUpdateAsync writes straight to the database, bypassing EF's change
                // tracker entirely - the `settings` instance already loaded above (and its
                // identity-map entry, which anything else resolving this same tracked entity
                // within the current scope would receive) would otherwise keep reporting the
                // pre-update value even though the row itself is now correct.
                if (dbContext is DbContext efContext)
                {
                    await efContext.Entry(settings).ReloadAsync(cancellationToken);
                }

                logger.LogInformation("Notion webhook verification token received and protected");
                return new(200, "Verification token received.");
            }

            var settingsRow = await dbContext.NotionConnectorSettings
                .FirstOrDefaultAsync(cancellationToken);
            if (settingsRow is null || string.IsNullOrWhiteSpace(settingsRow.WebhookVerificationToken))
            {
                return new(503, "Notion webhook verification has not been completed.");
            }

            string verificationSecret;
            try
            {
                verificationSecret = secretProtector.Unprotect(settingsRow.WebhookVerificationToken);
            }
            catch (CryptographicException)
            {
                logger.LogError("The stored Notion webhook verification token could not be decrypted");
                return new(503, "The stored webhook verification token is unavailable.");
            }

            if (!IsValidSignature(rawBody, signature, verificationSecret))
            {
                return new(401, "Invalid Notion webhook signature.");
            }

            var eventId = ReadString(root, "id");
            var eventType = ReadString(root, "type");
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(eventType))
            {
                return new(400, "Webhook event id and type are required.");
            }

            var workspaceId = ReadString(root, "workspace_id");
            if (!string.IsNullOrWhiteSpace(settingsRow.WorkspaceId)
                && !string.Equals(
                    NormalizeId(settingsRow.WorkspaceId),
                    NormalizeId(workspaceId),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new(403, "Webhook workspace does not match the connected workspace.");
            }

            var existing = await dbContext.NotionWebhookEvents
                .AsNoTracking()
                .AnyAsync(item => item.NotionEventId == eventId, cancellationToken);
            if (existing)
            {
                return new(200, "Webhook event already processed.");
            }

            var queued = syncCoordinator.TryQueueWebhookSync();
            var entity = root.TryGetProperty("entity", out var entityElement)
                && entityElement.ValueKind == JsonValueKind.Object
                    ? entityElement
                    : default;
            dbContext.NotionWebhookEvents.Add(new NotionWebhookEvent
            {
                NotionEventId = eventId,
                EventType = eventType,
                WorkspaceId = workspaceId,
                EntityType = ReadString(entity, "type"),
                EntityId = ReadString(entity, "id"),
                EventTimestamp = DateTimeOffset.TryParse(ReadString(root, "timestamp"), out var timestamp)
                    ? timestamp
                    : DateTimeOffset.UtcNow,
                SyncQueued = queued,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = "notion-webhook"
            });
            settingsRow.LastWebhookReceivedAt = DateTimeOffset.UtcNow;
            settingsRow.LastWebhookEventType = eventType;
            settingsRow.UpdatedAt = DateTimeOffset.UtcNow;
            settingsRow.UpdatedBy = "notion-webhook";
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Accepted Notion webhook {EventId} ({EventType}); sync queued: {SyncQueued}",
                eventId,
                eventType,
                queued);
            return new(200, queued
                ? "Webhook accepted and refresh queued."
                : "Webhook accepted; an existing refresh already covers the latest state.");
        }
    }

    private static bool IsValidSignature(string rawBody, string? signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(signature)
            || !signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(rawBody));
        var expected = $"sha256={Convert.ToHexString(expectedBytes).ToLowerInvariant()}";
        var suppliedBytes = Encoding.ASCII.GetBytes(signature);
        var expectedSignatureBytes = Encoding.ASCII.GetBytes(expected);
        return suppliedBytes.Length == expectedSignatureBytes.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedSignatureBytes);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string NormalizeId(string? value) =>
        Guid.TryParse(value, out var id) ? id.ToString("N") : value?.Trim() ?? string.Empty;
}
