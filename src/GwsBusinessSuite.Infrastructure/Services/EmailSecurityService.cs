using GwsBusinessSuite.Application.ThreatIntel;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Collects the public DNS answers (over HTTPS) and the MTA-STS policy file for a mail domain,
// then hands them to EmailSecurityEvaluator. All sources verified live 2026-10-05 against
// gwsapp.net and gmail.com. A lookup that fails is recorded in Errors and treated as "absent".
public sealed class EmailSecurityService(
    IDnsRecordLookup dns,
    HttpClient httpClient,
    ILogger<EmailSecurityService> logger) : IEmailSecurityService
{
    public async Task<EmailSecurityReport> CheckAsync(string domain, CancellationToken cancellationToken = default)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        var errors = new List<string>();

        async Task<IReadOnlyList<string>> Query(string name, string type)
        {
            try { return await dns.QueryAsync(name, type, cancellationToken); }
            catch (HttpRequestException ex)
            {
                logger.LogDebug(ex, "Email security DNS lookup {Type} {Name} failed", type, name);
                lock (errors) errors.Add($"Couldn't look up {type} for {name}");
                return [];
            }
        }

        var mxTask = Query(domain, "MX");
        var rootTask = Query(domain, "TXT");
        var dmarcTask = Query($"_dmarc.{domain}", "TXT");
        var stsTask = Query($"_mta-sts.{domain}", "TXT");
        var tlsRptTask = Query($"_smtp._tls.{domain}", "TXT");
        var dkimTasks = EmailSecurityEvaluator.CommonDkimSelectors
            .Select(async selector => (Selector: selector, Records: await Query($"{selector}._domainkey.{domain}", "TXT")))
            .ToList();
        await Task.WhenAll(mxTask, rootTask, dmarcTask, stsTask, tlsRptTask, Task.WhenAll(dkimTasks));

        var mxHosts = (await mxTask)
            .Select(mx => mx.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.TrimEnd('.'))
            .OfType<string>()
            .Where(host => host.Length > 0)
            .ToList();
        var dkimFound = dkimTasks
            .Select(task => task.Result)
            .Where(result => result.Records.Any(r => r.Contains("p=", StringComparison.OrdinalIgnoreCase)))
            .Select(result => result.Selector)
            .ToList();

        var stsTxt = await stsTask;
        string? policy = null;
        if (stsTxt.Any(t => t.StartsWith("v=STSv1", StringComparison.OrdinalIgnoreCase)))
        {
            policy = await TryGetMtaStsPolicyAsync(domain, cancellationToken);
        }

        return EmailSecurityEvaluator.Evaluate(new EmailSecurityEvaluator.Inputs(
            domain, mxHosts, await rootTask, await dmarcTask, dkimFound, stsTxt, policy, await tlsRptTask), errors);
    }

    private async Task<string?> TryGetMtaStsPolicyAsync(string domain, CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync($"https://mta-sts.{domain}/.well-known/mta-sts.txt", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "MTA-STS policy fetch failed for {Domain}", domain);
            return null;
        }
    }
}
