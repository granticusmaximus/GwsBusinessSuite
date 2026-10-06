using System.Net;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ThreatIntel;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// Findings are keyed "<source>:<subject>:<family>:<detail>". After each check family runs
// successfully, unresolved findings with that family's prefix that weren't produced again are
// marked resolved. "Event" families (a newly seen hostname/port/DNS change, a newly published
// CVE) are things that happened, not states, so they never auto-resolve - acknowledge them.
public sealed class ThreatMonitorService(
    IAppDbContext dbContext,
    IDomainIntelService domainIntel,
    IExposureIntelService exposureIntel,
    IMalwareFeedService malwareFeeds,
    IVulnerabilityIntelService vulnerabilities,
    IEmailSecurityService emailSecurity,
    IDependencyInventory dependencyInventory,
    IDependencyAdvisoryService dependencyAdvisories,
    IMailTransport mailTransport,
    IOptions<GrowthReportEmailOptions> smtpOptions,
    TimeProvider timeProvider,
    ILogger<ThreatMonitorService> logger) : IThreatMonitorService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> EventFamilies = ["new-host", "new-port", "dns-change", "nvd"];

    // NVD allows 5 requests per 30 seconds without an API key.
    public TimeSpan NvdRequestSpacing { get; init; } = TimeSpan.FromSeconds(7);

    // How far back a KEV entry still counts as news for a stack item; older catalog entries for a
    // big vendor (hundreds for "Microsoft") would bury anything current.
    public const int StackKevLookbackDays = 365;
    public const int StackNvdLookbackDays = 30;

    // ---- Assets ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<WatchAssetView>> ListAssetsAsync(CancellationToken cancellationToken = default)
    {
        var assets = await dbContext.ThreatWatchAssets.AsNoTracking().ToListAsync(cancellationToken);
        var open = await OpenFindingCountsAsync(cancellationToken);
        return assets.OrderBy(a => a.Value)
            .Select(a => new WatchAssetView(a.Id, a.Value, a.Kind, a.Label, a.LastCheckedAt, a.LastError,
                open.Where(kv => kv.Key.Contains($":{KeySubject(a.Value)}:", StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value)))
            .ToList();
    }

    public async Task<WatchAssetView> AddAssetAsync(string value, string? label, string username, CancellationToken cancellationToken = default)
    {
        var indicator = ThreatIndicatorClassifier.Classify(value);
        var kind = indicator.Kind switch
        {
            IndicatorKind.Domain => ThreatAssetKinds.Domain,
            IndicatorKind.IpAddress => ThreatAssetKinds.Ip,
            IndicatorKind.Url => ThreatAssetKinds.Domain,
            _ => throw new ArgumentException("Enter a domain (example.com) or a public IP address.")
        };
        var normalized = indicator.Host!;
        if (kind == ThreatAssetKinds.Ip && !IsPublic(IPAddress.Parse(normalized)))
            throw new ArgumentException("That's a private or reserved IP - only public addresses can be checked from the internet.");
        if (await dbContext.ThreatWatchAssets.AnyAsync(a => a.Value == normalized, cancellationToken))
            throw new ArgumentException($"{normalized} is already being watched.");

        await EnsureSettingsAsync(cancellationToken);
        var asset = new ThreatWatchAsset
        {
            Value = normalized,
            Kind = kind,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            CreatedBy = username,
            CreatedAt = timeProvider.GetUtcNow()
        };
        dbContext.ThreatWatchAssets.Add(asset);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new WatchAssetView(asset.Id, asset.Value, asset.Kind, asset.Label, null, null, 0);
    }

    public async Task RemoveAssetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var asset = await dbContext.ThreatWatchAssets.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (asset is null) return;
        dbContext.ThreatWatchAssets.Remove(asset);
        await RemoveFindingsWithPrefixAsync([$"{ThreatFindingSources.Asset}:{KeySubject(asset.Value)}:", $"{ThreatFindingSources.EmailSecurity}:{KeySubject(asset.Value)}:"], cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // ---- Stack ----------------------------------------------------------------------------

    public async Task<IReadOnlyList<StackItemView>> ListStackAsync(CancellationToken cancellationToken = default)
    {
        var items = await dbContext.ThreatStackItems.AsNoTracking().ToListAsync(cancellationToken);
        var open = await OpenFindingCountsAsync(cancellationToken);
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new StackItemView(i.Id, i.Name, i.Vendor, i.Product, i.Keywords,
                open.Where(kv => kv.Key.StartsWith($"{ThreatFindingSources.Stack}:{i.Id}:", StringComparison.Ordinal)).Sum(kv => kv.Value)))
            .ToList();
    }

    public async Task<StackItemView> AddStackItemAsync(string name, string? vendor, string? product, string? keywords, string username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the software a name.");
        await EnsureSettingsAsync(cancellationToken);
        var item = new ThreatStackItem
        {
            Name = name.Trim(),
            Vendor = Blank(vendor),
            Product = Blank(product),
            Keywords = Blank(keywords),
            CreatedBy = username,
            CreatedAt = timeProvider.GetUtcNow()
        };
        dbContext.ThreatStackItems.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new StackItemView(item.Id, item.Name, item.Vendor, item.Product, item.Keywords, 0);
    }

    public async Task RemoveStackItemAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.ThreatStackItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null) return;
        dbContext.ThreatStackItems.Remove(item);
        await RemoveFindingsWithPrefixAsync([$"{ThreatFindingSources.Stack}:{item.Id}:"], cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public IReadOnlyList<DependencyPackage> ListDependencies() => dependencyInventory.GetPackages();

    // ---- Findings -------------------------------------------------------------------------

    public async Task<IReadOnlyList<ThreatFindingView>> ListFindingsAsync(bool includeResolved = false, CancellationToken cancellationToken = default)
    {
        var query = dbContext.ThreatFindings.AsNoTracking();
        if (!includeResolved) query = query.Where(f => f.ResolvedAt == null);
        var rows = await query.ToListAsync(cancellationToken);
        return rows
            .OrderBy(f => f.ResolvedAt is null ? 0 : 1)
            .ThenBy(f => SeverityRank(f.Severity))
            .ThenByDescending(f => f.CreatedAt)
            .Select(ToView)
            .ToList();
    }

    public async Task AcknowledgeFindingAsync(Guid id, string username, CancellationToken cancellationToken = default)
    {
        var finding = await dbContext.ThreatFindings.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (finding is null) return;
        finding.AcknowledgedAt = timeProvider.GetUtcNow();
        finding.AcknowledgedBy = username;
        // An acknowledged event is done with; an acknowledged state stays visible until fixed.
        if (EventFamilies.Contains(FamilyOf(finding.Key))) finding.ResolvedAt ??= finding.AcknowledgedAt;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // ---- Settings -------------------------------------------------------------------------

    public async Task<ThreatMonitorSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await EnsureSettingsAsync(cancellationToken);
        return new ThreatMonitorSettingsView(settings.DigestEnabled, settings.DigestRecipient, settings.DigestHourLocal,
            settings.LastDigestSentAt, settings.LastAssetCheckAt, settings.LastStackCheckAt, settings.LastDependencyCheckAt,
            mailTransport.Describe(smtpOptions.Value).CanSend);
    }

    public async Task SaveSettingsAsync(bool digestEnabled, string? digestRecipient, int digestHourLocal, CancellationToken cancellationToken = default)
    {
        var recipient = Blank(digestRecipient);
        if (digestEnabled && (recipient is null || !MailboxAddress.TryParse(recipient, out _)))
            throw new ArgumentException("Enter a valid email address for the digest.");
        var settings = await EnsureSettingsAsync(cancellationToken);
        settings.DigestEnabled = digestEnabled;
        settings.DigestRecipient = recipient;
        settings.DigestHourLocal = Math.Clamp(digestHourLocal, 0, 23);
        settings.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // ---- Checks ---------------------------------------------------------------------------

    public async Task<ThreatCheckResult> RunChecksAsync(ThreatCheckScope scope, CancellationToken cancellationToken = default)
    {
        var tally = new Tally();
        var settings = await EnsureSettingsAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        if (scope.HasFlag(ThreatCheckScope.Assets))
        {
            foreach (var asset in await dbContext.ThreatWatchAssets.ToListAsync(cancellationToken))
            {
                await CheckAssetAsync(asset, tally, cancellationToken);
            }
            settings.LastAssetCheckAt = now;
        }

        if (scope.HasFlag(ThreatCheckScope.Stack))
        {
            await CheckStackAsync(tally, cancellationToken);
            settings.LastStackCheckAt = now;
        }

        if (scope.HasFlag(ThreatCheckScope.Dependencies))
        {
            await CheckDependenciesAsync(tally, cancellationToken);
            settings.LastDependencyCheckAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new ThreatCheckResult(tally.New, tally.Resolved, tally.Errors);
    }

    private async Task CheckAssetAsync(ThreatWatchAsset asset, Tally tally, CancellationToken ct)
    {
        var prefix = $"{ThreatFindingSources.Asset}:{KeySubject(asset.Value)}";
        var previous = DeserializeSnapshot(asset.SnapshotJson);
        var snapshot = new AssetSnapshot();
        var errors = new List<string>();
        var isFirstCheck = asset.LastCheckedAt is null;

        var indicator = ThreatIndicatorClassifier.Classify(asset.Value);
        var exposureTask = exposureIntel.InvestigateAsync(asset.Value, ct);
        var matchesTask = malwareFeeds.FindMatchesAsync(indicator, ct);
        var domainTask = asset.Kind == ThreatAssetKinds.Domain ? domainIntel.InvestigateAsync(asset.Value, ct) : null;
        var emailTask = asset.Kind == ThreatAssetKinds.Domain ? emailSecurity.CheckAsync(asset.Value, ct) : null;
        try
        {
            await Task.WhenAll(new Task?[] { exposureTask, matchesTask, domainTask, emailTask }.OfType<Task>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Checking watched asset {Asset} failed", asset.Value);
            asset.LastError = "The check failed - it'll be retried on the next run.";
            tally.Errors.Add($"{asset.Value}: check failed");
            return;
        }

        var exposure = await exposureTask;
        errors.AddRange(exposure.Errors);

        // Threat feeds - critical: the business's own asset is being used in attacks or phishing.
        var feedFindings = (await matchesTask)
            .GroupBy(m => m.Feed)
            .Select(g => new FindingCandidate($"{prefix}:feeds:{Slug(g.Key)}", ThreatFindingSeverities.Critical,
                $"{asset.Value} is listed on {g.Key}",
                $"{g.Count()} recent entr{(g.Count() == 1 ? "y" : "ies")}, e.g. {ThreatInvestigationMarkdown.Defang(g.First().MatchedValue)} - {g.First().Description}. If this isn't expected, the site or server may be compromised.",
                g.First().ReferenceUrl))
            .ToList();
        await ApplyAsync($"{prefix}:feeds:", feedFindings, tally, ct);

        if (domainTask is not null)
        {
            var domain = await domainTask;
            errors.AddRange(domain.Errors);

            var cert = new List<FindingCandidate>();
            if (domain.TlsCertificate is { } tls)
            {
                var days = (tls.NotAfter - timeProvider.GetUtcNow()).TotalDays;
                snapshot.CertificateExpires = tls.NotAfter;
                if (days < 0)
                    cert.Add(new($"{prefix}:cert:expired", ThreatFindingSeverities.Critical, $"TLS certificate for {asset.Value} has expired",
                        $"Expired {tls.NotAfter:yyyy-MM-dd}. Browsers now show a security warning.", null));
                else if (days < 14)
                    cert.Add(new($"{prefix}:cert:expiring", ThreatFindingSeverities.High, $"TLS certificate for {asset.Value} expires in {Math.Floor(days)} days",
                        $"Expires {tls.NotAfter:yyyy-MM-dd}. Check that automatic renewal is working.", null));
                else if (days < 30)
                    cert.Add(new($"{prefix}:cert:expiring", ThreatFindingSeverities.Medium, $"TLS certificate for {asset.Value} expires in {Math.Floor(days)} days",
                        $"Expires {tls.NotAfter:yyyy-MM-dd}.", null));
                await ApplyAsync($"{prefix}:cert:", cert, tally, ct);
            }

            if (domain.Registration?.Expires is { } registrationExpires)
            {
                var days = (registrationExpires - timeProvider.GetUtcNow()).TotalDays;
                var registration = days < 30
                    ? new List<FindingCandidate>
                    {
                        new($"{prefix}:registration:expiring", days < 7 ? ThreatFindingSeverities.Critical : ThreatFindingSeverities.High,
                            $"Domain registration for {asset.Value} expires in {Math.Max(0, Math.Floor(days))} days",
                            $"Expires {registrationExpires:yyyy-MM-dd}. A lapsed domain can be registered by anyone.", null)
                    }
                    : [];
                await ApplyAsync($"{prefix}:registration:", registration, tally, ct);
            }

            snapshot.Addresses = domain.DnsRecords.Where(r => r.RecordType is "A" or "AAAA").Select(r => r.Value).Order().ToList();
            if (!isFirstCheck && previous.Addresses is { Count: > 0 } before && snapshot.Addresses.Count > 0
                && !before.SequenceEqual(snapshot.Addresses))
            {
                await AddEventAsync($"{prefix}:dns-change:{Hash(string.Join(',', snapshot.Addresses))}", ThreatFindingSeverities.Medium,
                    $"{asset.Value} now points somewhere else",
                    $"A/AAAA changed from {string.Join(", ", before)} to {string.Join(", ", snapshot.Addresses)}. Expected if you moved hosting; otherwise check your DNS account.",
                    tally, ct);
            }

            snapshot.Hostnames = exposure.CertificateHostnames.Select(h => h.ToLowerInvariant()).Distinct().Order().ToList();
            if (snapshot.Hostnames.Count == 0 && previous.Hostnames is { Count: > 0 })
            {
                // Certificate log lookup was rate-limited or down this run - keep the old baseline.
                snapshot.Hostnames = previous.Hostnames;
            }
            else if (!isFirstCheck && previous.Hostnames is { } knownHosts)
            {
                foreach (var host in snapshot.Hostnames.Except(knownHosts).Take(20))
                {
                    await AddEventAsync($"{prefix}:new-host:{host}", ThreatFindingSeverities.Low,
                        $"New certificate issued for {host}",
                        "A certificate naming this hostname appeared in public Certificate Transparency logs. Expected if you just set it up; an unknown one can mean someone else got a certificate for your domain.",
                        tally, ct);
                }
            }

            if (emailTask is not null)
            {
                var email = await emailTask;
                var emailPrefix = $"{ThreatFindingSources.EmailSecurity}:{KeySubject(asset.Value)}";
                if (email.Errors.Count == 0)
                {
                    var emailFindings = email.Issues
                        .Where(i => i.Severity != ThreatFindingSeverities.Info)
                        .Select(i => new FindingCandidate($"{emailPrefix}:posture:{i.Code}", i.Severity, $"{asset.Value}: {i.Title}", i.Detail, null))
                        .ToList();
                    await ApplyAsync($"{emailPrefix}:posture:", emailFindings, tally, ct);
                }
                else errors.AddRange(email.Errors);
            }
        }
        else
        {
            if (exposure.IpExposure is { } ip)
            {
                snapshot.Ports = ip.Ports.Order().ToList();
                if (!isFirstCheck && previous.Ports is { } knownPorts)
                {
                    foreach (var port in snapshot.Ports.Except(knownPorts))
                    {
                        await AddEventAsync($"{prefix}:new-port:{port}", ThreatFindingSeverities.Medium,
                            $"Port {port} is now open on {asset.Value}",
                            "Shodan's scanners saw a service on this port that wasn't there last check. Close it if it isn't meant to be public.",
                            tally, ct);
                    }
                }

                var cves = ip.Vulnerabilities.Take(50)
                    .Select(cve => new FindingCandidate($"{prefix}:cves:{cve}", ThreatFindingSeverities.High,
                        $"{asset.Value} may be vulnerable to {cve}",
                        "Shodan matched the software version this server exposes to a known vulnerability. Confirm the version and patch.",
                        $"https://nvd.nist.gov/vuln/detail/{cve}"))
                    .ToList();
                await ApplyAsync($"{prefix}:cves:", cves, tally, ct);
            }

            var listings = new List<FindingCandidate>();
            if (exposure.SpamhausDrop is { } drop)
                listings.Add(new($"{prefix}:blocklist:spamhaus-drop", ThreatFindingSeverities.Critical, $"{asset.Value} is on Spamhaus DROP",
                    $"Its network {drop.Cidr} ({drop.SblId}) is listed as hijacked or criminal-controlled; much of the internet will drop traffic from it.",
                    $"https://check.spamhaus.org/sbl/listings/{drop.SblId}"));
            if (exposure.IsTorExitNode == true)
                listings.Add(new($"{prefix}:blocklist:tor-exit", ThreatFindingSeverities.Medium, $"{asset.Value} is a Tor exit node",
                    "Many services block or challenge Tor exit addresses.", null));
            if (exposure.IsTorExitNode is not null) await ApplyAsync($"{prefix}:blocklist:", listings, tally, ct);
        }

        asset.SnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions);
        asset.LastCheckedAt = timeProvider.GetUtcNow();
        asset.LastError = errors.Count == 0 ? null : string.Join("; ", errors.Distinct().Take(4));
    }

    private async Task CheckStackAsync(Tally tally, CancellationToken ct)
    {
        var items = await dbContext.ThreatStackItems.ToListAsync(ct);
        if (items.Count == 0) return;

        var kev = await vulnerabilities.GetKnownExploitedAsync(5000, ct);
        var kevCutoff = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime.AddDays(-StackKevLookbackDays));
        var firstNvdCall = true;

        foreach (var item in items)
        {
            var prefix = $"{ThreatFindingSources.Stack}:{item.Id}";
            var kevFindings = kev
                .Where(entry => entry.DateAdded >= kevCutoff && MatchesKev(item, entry))
                .Select(entry => new FindingCandidate($"{prefix}:kev:{entry.CveId}",
                    entry.KnownRansomwareUse ? ThreatFindingSeverities.Critical : ThreatFindingSeverities.High,
                    $"{item.Name}: {entry.CveId} is being exploited in the wild",
                    $"{entry.Vendor} {entry.Product} - {entry.Name}. CISA added it {entry.DateAdded:yyyy-MM-dd}. {entry.RequiredAction}",
                    $"https://nvd.nist.gov/vuln/detail/{entry.CveId}"))
                .ToList();
            await ApplyAsync($"{prefix}:kev:", kevFindings, tally, ct);

            foreach (var keyword in KeywordsFor(item))
            {
                if (!firstNvdCall) await Task.Delay(NvdRequestSpacing, timeProvider, ct);
                firstNvdCall = false;
                IReadOnlyList<CveSummary> recent;
                try
                {
                    recent = await vulnerabilities.SearchRecentCvesAsync(keyword, StackNvdLookbackDays, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    logger.LogWarning(ex, "NVD search for {Keyword} failed", keyword);
                    tally.Errors.Add($"NVD search for \"{keyword}\" failed");
                    continue;
                }

                var serious = recent.Where(c => c.CvssScore >= 7.0).ToList();
                var epss = await vulnerabilities.GetEpssScoresAsync(serious.Select(c => c.CveId), ct);
                foreach (var cve in serious)
                {
                    var epssText = epss.TryGetValue(cve.CveId, out var score) ? $" EPSS {score:P1}." : "";
                    await AddEventAsync($"{prefix}:nvd:{cve.CveId}",
                        cve.CvssScore >= 9.0 ? ThreatFindingSeverities.High : ThreatFindingSeverities.Medium,
                        $"{item.Name}: new {cve.CvssSeverity?.ToLowerInvariant() ?? "high"}-severity {cve.CveId} (CVSS {cve.CvssScore:0.0})",
                        $"{Truncate(cve.Description, 400)} Matched the keyword \"{keyword}\" - check whether it applies to the version you run.{epssText}",
                        tally, ct, $"https://nvd.nist.gov/vuln/detail/{cve.CveId}");
                }
            }
        }
    }

    private async Task CheckDependenciesAsync(Tally tally, CancellationToken ct)
    {
        var packages = dependencyInventory.GetPackages();
        if (packages.Count == 0)
        {
            tally.Errors.Add("No dependency list was found for this build");
            return;
        }

        IReadOnlyList<DependencyAdvisory> advisories;
        try
        {
            advisories = await dependencyAdvisories.FindAdvisoriesAsync(packages, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "OSV dependency check failed");
            tally.Errors.Add("OSV.dev couldn't be reached");
            return;
        }

        var findings = advisories
            .Select(a => new FindingCandidate(
                $"{ThreatFindingSources.Dependency}:{a.Package.Name}:advisory:{a.Package.Ecosystem}:{a.Package.Version}:{a.Id}",
                a.Severity,
                $"{a.Package.Name} {a.Package.Version}: {a.Summary ?? a.Id}",
                $"{a.Package.Ecosystem} package used by this app. {(a.FixedVersion is { } fixedIn ? $"Fixed in {fixedIn} - upgrade the package." : "No fixed version published yet.")}{(a.Aliases.Count > 0 ? $" Also known as {string.Join(", ", a.Aliases)}." : "")}",
                a.ReferenceUrl))
            .ToList();
        // One prefix for all dependency findings: an upgraded package resolves its old findings.
        await ApplyAsync($"{ThreatFindingSources.Dependency}:", findings, tally, ct);
    }

    // ---- Digest ---------------------------------------------------------------------------

    public async Task<DigestSendResult> SendDigestAsync(bool force, CancellationToken cancellationToken = default)
    {
        var settings = await EnsureSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.DigestRecipient) || !MailboxAddress.TryParse(settings.DigestRecipient, out var to))
            return new(false, "Set a digest email address first.");
        var smtp = smtpOptions.Value;
        if (!mailTransport.Describe(smtp).CanSend || !MailboxAddress.TryParse(smtp.FromAddress, out var from))
            return new(false, "Email delivery isn't set up on this server (Settings > Email).");

        var now = timeProvider.GetUtcNow();
        var fresh = (await dbContext.ThreatFindings.Where(f => f.DigestedAt == null && f.ResolvedAt == null).ToListAsync(cancellationToken))
            .OrderBy(f => SeverityRank(f.Severity)).ThenByDescending(f => f.CreatedAt).ToList();
        var since = settings.LastDigestSentAt ?? now.AddDays(-1);
        var kevAdded = (await vulnerabilities.GetKnownExploitedAsync(100, cancellationToken))
            .Where(k => k.DateAdded >= DateOnly.FromDateTime(since.UtcDateTime))
            .ToList();
        var open = await dbContext.ThreatFindings.CountAsync(f => f.ResolvedAt == null, cancellationToken);

        if (!force && fresh.Count == 0 && kevAdded.Count == 0)
        {
            settings.LastDigestSentAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(false, "Nothing new since the last digest.");
        }

        var message = new MimeMessage();
        message.From.Add(from);
        message.To.Add(to);
        message.Subject = fresh.Count > 0
            ? $"[GWS Threat Digest] {fresh.Count} new finding{(fresh.Count == 1 ? "" : "s")}{(fresh.Any(f => f.Severity == ThreatFindingSeverities.Critical) ? " (critical)" : "")}"
            : $"[GWS Threat Digest] {kevAdded.Count} newly exploited vulnerabilit{(kevAdded.Count == 1 ? "y" : "ies")}";
        message.Body = new BodyBuilder
        {
            TextBody = BuildDigestText(fresh, kevAdded, open),
            HtmlBody = BuildDigestHtml(fresh, kevAdded, open)
        }.ToMessageBody();

        await mailTransport.SendAsync(message, smtp, "threat-digest", cancellationToken);
        foreach (var finding in fresh) finding.DigestedAt = now;
        settings.LastDigestSentAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(true, $"Digest sent to {to.Address}.");
    }

    internal static string BuildDigestText(IReadOnlyList<ThreatFinding> fresh, IReadOnlyList<KnownExploitedVulnerability> kevAdded, int openCount)
    {
        var text = new StringBuilder();
        text.AppendLine($"New findings: {fresh.Count} (open in total: {openCount})");
        text.AppendLine();
        foreach (var f in fresh)
        {
            text.AppendLine($"[{f.Severity.ToUpperInvariant()}] {f.Title}");
            if (!string.IsNullOrWhiteSpace(f.Detail)) text.AppendLine($"    {f.Detail}");
            if (!string.IsNullOrWhiteSpace(f.ReferenceUrl)) text.AppendLine($"    {f.ReferenceUrl}");
        }
        if (kevAdded.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"Newly added to CISA's Known Exploited Vulnerabilities ({kevAdded.Count}):");
            foreach (var k in kevAdded.Take(15))
                text.AppendLine($"- {k.CveId} {k.Vendor} {k.Product}: {k.Name}{(k.KnownRansomwareUse ? " (used by ransomware)" : "")}");
        }
        text.AppendLine();
        text.AppendLine("Review and acknowledge findings under Intelligence > Threat Intelligence > My exposure.");
        return text.ToString();
    }

    internal static string BuildDigestHtml(IReadOnlyList<ThreatFinding> fresh, IReadOnlyList<KnownExploitedVulnerability> kevAdded, int openCount)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var html = new StringBuilder("<div style=\"font-family:system-ui,sans-serif;font-size:14px;color:#1f2937\">");
        html.Append($"<h2 style=\"margin:0 0 4px\">Threat digest</h2><p style=\"color:#64748b;margin:0 0 16px\">{fresh.Count} new finding(s), {openCount} open in total.</p>");
        foreach (var f in fresh)
        {
            var color = f.Severity switch { "critical" => "#b91c1c", "high" => "#c2410c", "medium" => "#a16207", _ => "#475569" };
            html.Append($"<div style=\"border-left:4px solid {color};padding:6px 10px;margin:0 0 10px;background:#f8fafc\">")
                .Append($"<strong style=\"color:{color};text-transform:uppercase;font-size:11px\">{E(f.Severity)}</strong><br><strong>{E(f.Title)}</strong>");
            if (!string.IsNullOrWhiteSpace(f.Detail)) html.Append($"<br><span>{E(f.Detail)}</span>");
            if (!string.IsNullOrWhiteSpace(f.ReferenceUrl)) html.Append($"<br><a href=\"{E(f.ReferenceUrl)}\">Details</a>");
            html.Append("</div>");
        }
        if (kevAdded.Count > 0)
        {
            html.Append($"<h3>Newly exploited vulnerabilities (CISA KEV, {kevAdded.Count})</h3><ul>");
            foreach (var k in kevAdded.Take(15))
                html.Append($"<li><a href=\"https://nvd.nist.gov/vuln/detail/{E(k.CveId)}\">{E(k.CveId)}</a> {E(k.Vendor)} {E(k.Product)}: {E(k.Name)}{(k.KnownRansomwareUse ? " <strong>(ransomware)</strong>" : "")}</li>");
            html.Append("</ul>");
        }
        html.Append("<p style=\"color:#64748b\">Review and acknowledge findings under Intelligence &gt; Threat Intelligence &gt; My exposure.</p></div>");
        return html.ToString();
    }

    // ---- Finding bookkeeping --------------------------------------------------------------

    // Upserts every candidate, then resolves unresolved state findings under `prefix` that this
    // run didn't produce again.
    private async Task ApplyAsync(string prefix, IReadOnlyList<FindingCandidate> candidates, Tally tally, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var existing = await dbContext.ThreatFindings.Where(f => f.Key.StartsWith(prefix)).ToListAsync(ct);
        var byKey = existing.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var produced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (!produced.Add(candidate.Key)) continue;
            if (byKey.TryGetValue(candidate.Key, out var finding))
            {
                if (finding.ResolvedAt is not null)
                {
                    // It came back - treat it as new again.
                    finding.ResolvedAt = null;
                    finding.AcknowledgedAt = null;
                    finding.AcknowledgedBy = null;
                    finding.DigestedAt = null;
                    tally.New++;
                }
                finding.Severity = candidate.Severity;
                finding.Title = candidate.Title;
                finding.Detail = candidate.Detail;
                finding.ReferenceUrl = candidate.ReferenceUrl;
                finding.LastSeenAt = now;
            }
            else
            {
                dbContext.ThreatFindings.Add(NewFinding(candidate, now));
                tally.New++;
            }
        }

        foreach (var stale in existing.Where(f => f.ResolvedAt is null && !produced.Contains(f.Key) && !EventFamilies.Contains(FamilyOf(f.Key))))
        {
            stale.ResolvedAt = now;
            tally.Resolved++;
        }

        await dbContext.SaveChangesAsync(ct);
    }

    private async Task AddEventAsync(string key, string severity, string title, string detail, Tally tally, CancellationToken ct, string? referenceUrl = null)
    {
        if (await dbContext.ThreatFindings.AnyAsync(f => f.Key == key, ct)) return;
        dbContext.ThreatFindings.Add(NewFinding(new FindingCandidate(key, severity, title, detail, referenceUrl), timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(ct);
        tally.New++;
    }

    private static ThreatFinding NewFinding(FindingCandidate candidate, DateTimeOffset now)
    {
        var parts = candidate.Key.Split(':');
        return new ThreatFinding
        {
            Key = candidate.Key.Length <= 400 ? candidate.Key : candidate.Key[..400],
            Source = parts[0],
            Subject = parts.Length > 1 ? parts[1] : string.Empty,
            Severity = candidate.Severity,
            Title = candidate.Title,
            Detail = candidate.Detail,
            ReferenceUrl = candidate.ReferenceUrl,
            CreatedAt = now,
            LastSeenAt = now
        };
    }

    private async Task RemoveFindingsWithPrefixAsync(IEnumerable<string> prefixes, CancellationToken ct)
    {
        foreach (var prefix in prefixes)
        {
            dbContext.ThreatFindings.RemoveRange(await dbContext.ThreatFindings.Where(f => f.Key.StartsWith(prefix)).ToListAsync(ct));
        }
    }

    private async Task<Dictionary<string, int>> OpenFindingCountsAsync(CancellationToken ct) =>
        (await dbContext.ThreatFindings.AsNoTracking().Where(f => f.ResolvedAt == null).Select(f => f.Key).ToListAsync(ct))
            .GroupBy(key => key[..Math.Max(0, key.LastIndexOf(':'))])
            .ToDictionary(g => g.Key + ":", g => g.Count());

    private async Task<ThreatMonitorSettings> EnsureSettingsAsync(CancellationToken ct)
    {
        var settings = await dbContext.ThreatMonitorSettings.FirstOrDefaultAsync(ct);
        if (settings is not null) return settings;
        settings = new ThreatMonitorSettings { CreatedAt = timeProvider.GetUtcNow() };
        dbContext.ThreatMonitorSettings.Add(settings);
        await dbContext.SaveChangesAsync(ct);
        return settings;
    }

    // ---- Helpers --------------------------------------------------------------------------

    // Keys are colon-separated, so an IPv6 subject has its colons swapped out.
    internal static string KeySubject(string value) => value.Replace(':', '_');

    // Key shape: source:subject:family:detail - the family is the third segment.
    internal static string FamilyOf(string key)
    {
        var parts = key.Split(':');
        return parts.Length > 2 ? parts[2] : string.Empty;
    }

    internal static bool MatchesKev(ThreatStackItem item, KnownExploitedVulnerability entry)
    {
        if (item.Vendor is not null || item.Product is not null)
        {
            var vendorOk = item.Vendor is null || entry.Vendor.Contains(item.Vendor, StringComparison.OrdinalIgnoreCase);
            var productOk = item.Product is null || entry.Product.Contains(item.Product, StringComparison.OrdinalIgnoreCase);
            return vendorOk && productOk;
        }
        return entry.Product.Contains(item.Name, StringComparison.OrdinalIgnoreCase)
            || entry.Vendor.Contains(item.Name, StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> KeywordsFor(ThreatStackItem item) =>
        (string.IsNullOrWhiteSpace(item.Keywords) ? item.Name : item.Keywords)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

    internal static int SeverityRank(string severity) => severity switch
    {
        ThreatFindingSeverities.Critical => 0,
        ThreatFindingSeverities.High => 1,
        ThreatFindingSeverities.Medium => 2,
        ThreatFindingSeverities.Low => 3,
        _ => 4
    };

    private static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return false;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] >= 224 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127));
        }
        return true;
    }

    private static ThreatFindingView ToView(ThreatFinding f) =>
        new(f.Id, f.Source, f.Subject, f.Severity, f.Title, f.Detail, f.ReferenceUrl, f.CreatedAt, f.LastSeenAt, f.ResolvedAt, f.AcknowledgedAt, f.AcknowledgedBy);

    private static AssetSnapshot DeserializeSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AssetSnapshot();
        try { return JsonSerializer.Deserialize<AssetSnapshot>(json, JsonOptions) ?? new AssetSnapshot(); }
        catch (JsonException) { return new AssetSnapshot(); }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Slug(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string Truncate(string? value, int max) => value is null ? string.Empty : value.Length <= max ? value : value[..max] + "…";
    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();

    internal sealed class AssetSnapshot
    {
        public DateTimeOffset? CertificateExpires { get; set; }
        public List<string>? Addresses { get; set; }
        public List<string>? Hostnames { get; set; }
        public List<int>? Ports { get; set; }
    }

    private sealed class Tally
    {
        public int New { get; set; }
        public int Resolved { get; set; }
        public List<string> Errors { get; } = [];
    }
}
