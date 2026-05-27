using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.AppRegistry;

public sealed class AppRegistryService(IAppDbContext dbContext) : IAppRegistryService
{
    public async Task<IReadOnlyList<BusinessApp>> ListAppsAsync(CancellationToken cancellationToken = default)
    {
        var apps = await dbContext.BusinessApps
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return apps
            .OrderByDescending(app => app.UpdatedAt ?? app.CreatedAt)
            .ThenBy(app => app.Name)
            .ToList();
    }

    public async Task<BusinessApp?> GetAppAsync(Guid appId, CancellationToken cancellationToken = default)
    {
        return await dbContext.BusinessApps
            .AsNoTracking()
            .FirstOrDefaultAsync(app => app.Id == appId, cancellationToken);
    }

    public async Task<BusinessApp> SaveAppAsync(AppRegistryEditorModel editor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var now = DateTimeOffset.UtcNow;
        var app = editor.AppId is { } appId
            ? await dbContext.BusinessApps.FirstOrDefaultAsync(item => item.Id == appId, cancellationToken)
            : null;

        var isNew = app is null;
        app ??= new BusinessApp
        {
            Name = string.Empty,
            AppType = "WebsiteCms",
            CreatedAt = now,
            CreatedBy = "app-registry-ui"
        };

        var normalizedName = editor.Name.Trim();
        var normalizedAppType = string.IsNullOrWhiteSpace(editor.AppType) ? "WebsiteCms" : editor.AppType.Trim();
        var normalizedStatus = string.IsNullOrWhiteSpace(editor.Status) ? "Draft" : editor.Status.Trim();
        var normalizedSubdomain = NormalizeSubdomain(editor.Subdomain);

        if (!string.IsNullOrWhiteSpace(normalizedSubdomain))
        {
            var duplicateSubdomainExists = await dbContext.BusinessApps
                .AnyAsync(item => item.Id != app.Id && item.Subdomain != null && item.Subdomain.ToLower() == normalizedSubdomain,
                    cancellationToken);

            if (duplicateSubdomainExists)
            {
                throw new InvalidOperationException($"Subdomain '{normalizedSubdomain}' is already in use by another app.");
            }
        }

        app.Name = normalizedName;
        app.AppType = normalizedAppType;
        app.Subdomain = string.IsNullOrWhiteSpace(normalizedSubdomain) ? null : normalizedSubdomain;
        app.Status = normalizedStatus;
        app.Port = editor.Port;
        app.UpdatedAt = now;
        app.UpdatedBy = "app-registry-ui";

        if (isNew)
        {
            await dbContext.BusinessApps.AddAsync(app, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return app;
    }

    public async Task DeleteAppAsync(Guid appId, CancellationToken cancellationToken = default)
    {
        var app = await dbContext.BusinessApps.FirstOrDefaultAsync(item => item.Id == appId, cancellationToken);
        if (app is null)
        {
            return;
        }

        dbContext.BusinessApps.Remove(app);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static string NormalizeSubdomain(string? subdomain)
    {
        if (string.IsNullOrWhiteSpace(subdomain))
        {
            return string.Empty;
        }

        var trimmed = subdomain.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(trimmed.Length);
        var previousDash = false;

        foreach (var character in trimmed)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousDash = false;
                continue;
            }

            if (character is '-' or '_' or ' ' or '.')
            {
                if (!previousDash)
                {
                    builder.Append('-');
                    previousDash = true;
                }
            }
        }

        return builder.ToString().Trim('-');
    }
}
