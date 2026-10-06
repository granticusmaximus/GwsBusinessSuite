namespace GwsBusinessSuite.Application.ThreatIntel;

// The packages this app actually ships: NuGet packages from the running app's .deps.json, plus
// the browser libraries it loads (vendored under wwwroot/lib or pinned on jsDelivr).
public interface IDependencyInventory
{
    IReadOnlyList<DependencyPackage> GetPackages();
}

// Known vulnerabilities in specific package versions, from OSV.dev (free, no key; covers GitHub
// Security Advisories for NuGet and npm).
public interface IDependencyAdvisoryService
{
    Task<IReadOnlyList<DependencyAdvisory>> FindAdvisoriesAsync(IReadOnlyList<DependencyPackage> packages, CancellationToken cancellationToken = default);
}

public sealed record DependencyAdvisory(
    DependencyPackage Package,
    string Id,
    string? Summary,
    string Severity,
    IReadOnlyList<string> Aliases,
    string? FixedVersion,
    string ReferenceUrl);
