using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Automation;

public sealed class AutomationCredentialService(
    IAppDbContext db,
    ISecretProtector secretProtector,
    TimeProvider timeProvider) : IAutomationCredentialService
{
    public async Task<IReadOnlyList<AutomationCredentialSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.AutomationCredentials.AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => new AutomationCredentialSummary(
                item.Id, item.Name, item.TypeKey, item.Description, item.LastUsedAt, item.UpdatedAt ?? item.CreatedAt))
            .ToListAsync(cancellationToken);

    public async Task<Guid> SaveAsync(
        Guid? id,
        string name,
        string typeKey,
        string credentialJson,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Credential name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(typeKey)) throw new ArgumentException("Credential type is required.", nameof(typeKey));
        try { System.Text.Json.JsonDocument.Parse(credentialJson).Dispose(); }
        catch (System.Text.Json.JsonException ex) { throw new ArgumentException($"Credential data must be valid JSON: {ex.Message}", nameof(credentialJson)); }

        AutomationCredential credential;
        if (id.HasValue)
        {
            credential = await db.AutomationCredentials.FirstOrDefaultAsync(item => item.Id == id.Value, cancellationToken)
                ?? throw new KeyNotFoundException("Credential was not found.");
        }
        else
        {
            credential = new AutomationCredential { Name = name.Trim(), TypeKey = typeKey.Trim(), CreatedBy = "user" };
            db.AutomationCredentials.Add(credential);
        }

        credential.Name = name.Trim();
        credential.TypeKey = typeKey.Trim();
        credential.ProtectedData = secretProtector.Protect(credentialJson);
        credential.Description = description?.Trim() ?? string.Empty;
        credential.UpdatedAt = timeProvider.GetUtcNow();
        credential.UpdatedBy = "user";
        await db.SaveChangesAsync(cancellationToken);
        return credential.Id;
    }

    public async Task<string?> GetDecryptedDataAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        var credential = await db.AutomationCredentials.FirstOrDefaultAsync(item => item.Id == credentialId, cancellationToken);
        if (credential is null) return null;
        credential.LastUsedAt = timeProvider.GetUtcNow();
        credential.UpdatedAt = credential.LastUsedAt;
        credential.UpdatedBy = "automation-engine";
        await db.SaveChangesAsync(cancellationToken);
        return secretProtector.Unprotect(credential.ProtectedData);
    }
}
