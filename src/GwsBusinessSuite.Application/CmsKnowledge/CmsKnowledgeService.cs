namespace GwsBusinessSuite.Application.CmsKnowledge;

public sealed class CmsKnowledgeService : ICmsKnowledgeService
{
    private static readonly IReadOnlyList<CmsKnowledgeSource> Sources =
    [
        new()
        {
            Key = "wp-clean-room",
            Name = "WordPress Workflow Reference (Clean Room)",
            LicenseNotes = "Do not copy source code or proprietary assets. Reimplement behavior only.",
            UsageGuidance = "Use as product behavior inspiration for workflows, content modeling, and admin UX."
        },
        new()
        {
            Key = "elementor-clean-room",
            Name = "Elementor Workflow Reference (Clean Room)",
            LicenseNotes = "Do not clone protected UI/brand assets. Build original controls and layouts.",
            UsageGuidance = "Use as inspiration for visual editing flow, section nesting, and style controls."
        }
    ];

    private static readonly IReadOnlyList<CmsKnowledgeEntry> Entries =
    [
        new()
        {
            SourceKey = "wp-clean-room",
            Capability = "Template hierarchy and routing",
            WorkflowSummary = "Resolve route to the best matching template with fallback layers.",
            ImplementationHint = "Model template precedence in application logic and store template metadata separately from page content.",
            SuggestedBlocks = ["template-slot", "dynamic-region", "route-layout"]
        },
        new()
        {
            SourceKey = "wp-clean-room",
            Capability = "Content revision workflow",
            WorkflowSummary = "Draft, review, approve, and publish content versions with audit history.",
            ImplementationHint = "Store immutable revisions and transition events so publish rollback remains safe.",
            SuggestedBlocks = ["revision-timeline", "approval-gate", "publish-status"]
        },
        new()
        {
            SourceKey = "elementor-clean-room",
            Capability = "Visual section/column composition",
            WorkflowSummary = "Construct pages from nested sections, columns, and widget blocks.",
            ImplementationHint = "Use JSON schema versioning for block trees and validate depth/width constraints.",
            SuggestedBlocks = ["section", "column", "widget-container"]
        },
        new()
        {
            SourceKey = "elementor-clean-room",
            Capability = "Responsive style controls",
            WorkflowSummary = "Define per-breakpoint spacing, typography, and visibility controls.",
            ImplementationHint = "Store style settings as breakpoint maps with a deterministic fallback chain.",
            SuggestedBlocks = ["responsive-style", "breakpoint-rule", "visibility-toggle"]
        },
        new()
        {
            SourceKey = "wp-clean-room",
            Capability = "Plugin-like extension points",
            WorkflowSummary = "Allow modular feature packs to register capabilities without core rewrites.",
            ImplementationHint = "Add capability registration contracts and sandbox execution boundaries.",
            SuggestedBlocks = ["extension-hook", "capability-registration", "feature-toggle"]
        }
    ];

    public Task<IReadOnlyList<CmsKnowledgeSource>> ListSourcesAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Sources);
    }

    public Task<IReadOnlyList<CmsKnowledgeQueryResult>> SearchAsync(string query, int take = 5, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<CmsKnowledgeQueryResult>>([]);
        }

        var terms = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static x => x.ToLowerInvariant())
            .Distinct()
            .ToArray();

        var ranked = Entries
            .Select(entry => new CmsKnowledgeQueryResult
            {
                SourceKey = entry.SourceKey,
                Capability = entry.Capability,
                WorkflowSummary = entry.WorkflowSummary,
                ImplementationHint = entry.ImplementationHint,
                SuggestedBlocks = entry.SuggestedBlocks,
                Score = ScoreEntry(entry, terms)
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Capability)
            .Take(Math.Clamp(take, 1, 20))
            .ToList();

        return Task.FromResult<IReadOnlyList<CmsKnowledgeQueryResult>>(ranked);
    }

    private static int ScoreEntry(CmsKnowledgeEntry entry, IReadOnlyCollection<string> terms)
    {
        var score = 0;
        var capability = entry.Capability.ToLowerInvariant();
        var summary = entry.WorkflowSummary.ToLowerInvariant();
        var hint = entry.ImplementationHint.ToLowerInvariant();
        var blocks = string.Join(' ', entry.SuggestedBlocks).ToLowerInvariant();

        foreach (var term in terms)
        {
            if (capability.Contains(term, StringComparison.Ordinal))
            {
                score += 5;
            }

            if (summary.Contains(term, StringComparison.Ordinal))
            {
                score += 3;
            }

            if (hint.Contains(term, StringComparison.Ordinal))
            {
                score += 2;
            }

            if (blocks.Contains(term, StringComparison.Ordinal))
            {
                score += 1;
            }
        }

        return score;
    }
}
