using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Community;

// Lives directly in Application (not Infrastructure) against IAppDbContext, same precedent as
// RelatedArticlesService - no EF-provider dependency needed for straightforward CRUD/lookup work.
public sealed class CommunityDirectoryService(IAppDbContext dbContext) : ICommunityDirectoryService
{
    public async Task<IReadOnlyList<DepartmentView>> ListDepartmentsAsync(CancellationToken cancellationToken = default)
    {
        var departments = await dbContext.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync(cancellationToken);
        if (departments.Count == 0) return [];

        // Materialized then joined in-memory throughout this service - these are small,
        // internal-staff-scale tables (dozens to low hundreds of rows at most), and mixing
        // AppUsers/MemberProfiles/Departments into one server-translatable LINQ query buys
        // nothing here while risking the usual SQLite/EF Core client-eval surprises this
        // codebase has hit before with more complex query shapes.
        var profiles = await dbContext.MemberProfiles.AsNoTracking().ToListAsync(cancellationToken);
        var memberships = await dbContext.DepartmentMemberships.AsNoTracking().ToListAsync(cancellationToken);
        // Distinct people per department, whether it's their primary department or an extra one.
        var memberCounts = profiles
            .Where(p => p.DepartmentId.HasValue)
            .Select(p => (DepartmentId: p.DepartmentId!.Value, p.AppUserId))
            .Concat(memberships.Select(m => (m.DepartmentId, m.AppUserId)))
            .Distinct()
            .GroupBy(x => x.DepartmentId)
            .ToDictionary(g => g.Key, g => g.Count());

        var leadUsernames = departments.Where(d => !string.IsNullOrWhiteSpace(d.LeadUsername))
            .Select(d => d.LeadUsername!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var leadDisplayNames = await ResolveDisplayNamesAsync(leadUsernames, cancellationToken);

        return departments.Select(d => new DepartmentView(
            d.Id,
            d.Name,
            d.Description,
            d.LeadUsername,
            string.IsNullOrWhiteSpace(d.LeadUsername) ? null : leadDisplayNames.GetValueOrDefault(d.LeadUsername),
            memberCounts.GetValueOrDefault(d.Id)
        )).ToList();
    }

    public async Task<DepartmentView> SaveDepartmentAsync(DepartmentEditorModel editor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (string.IsNullOrWhiteSpace(editor.Name))
        {
            throw new ArgumentException("A department name is required.", nameof(editor));
        }

        var department = editor.DepartmentId is { } id
            ? await dbContext.Departments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            : null;
        var isNew = department is null;
        department ??= new Department { Name = editor.Name.Trim() };

        department.Name = editor.Name.Trim();
        department.Description = editor.Description?.Trim() ?? string.Empty;
        department.LeadUsername = string.IsNullOrWhiteSpace(editor.LeadUsername) ? null : editor.LeadUsername.Trim();
        department.UpdatedAt = DateTimeOffset.UtcNow;

        if (isNew) dbContext.Departments.Add(department);
        await dbContext.SaveChangesAsync(cancellationToken);

        var leadDisplayName = string.IsNullOrWhiteSpace(department.LeadUsername)
            ? null
            : (await ResolveDisplayNamesAsync([department.LeadUsername], cancellationToken)).GetValueOrDefault(department.LeadUsername);

        return new DepartmentView(department.Id, department.Name, department.Description, department.LeadUsername, leadDisplayName, 0);
    }

    public async Task DeleteDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default)
    {
        var department = await dbContext.Departments.FirstOrDefaultAsync(d => d.Id == departmentId, cancellationToken);
        if (department is null) return;

        // Members of a deleted department aren't deleted or blocked - they just fall back to
        // "no department" (a loose reference, see CommunityEntities.cs), same as any other
        // loose-reference cleanup in this codebase.
        var orphanedProfiles = await dbContext.MemberProfiles.Where(p => p.DepartmentId == departmentId).ToListAsync(cancellationToken);
        foreach (var profile in orphanedProfiles)
        {
            profile.DepartmentId = null;
        }
        dbContext.DepartmentMemberships.RemoveRange(
            await dbContext.DepartmentMemberships.Where(m => m.DepartmentId == departmentId).ToListAsync(cancellationToken));

        dbContext.Departments.Remove(department);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MemberProfileView>> ListProfilesAsync(
        Guid? departmentId = null, string? searchText = null, CancellationToken cancellationToken = default)
    {
        var users = await dbContext.AppUsers.AsNoTracking().Where(u => u.IsActive).ToListAsync(cancellationToken);
        var profilesByUserId = await dbContext.MemberProfiles.AsNoTracking().ToListAsync(cancellationToken);
        var profileLookup = profilesByUserId.ToDictionary(p => p.AppUserId);
        var departmentsByUser = await LoadDepartmentsAsync(profilesByUserId, null, cancellationToken);

        var views = users.Select(u =>
        {
            profileLookup.TryGetValue(u.Id, out var profile);
            return ToView(u, profile, departmentsByUser.GetValueOrDefault(u.Id) ?? []);
        });

        if (departmentId is { } filterDepartmentId)
        {
            views = views.Where(v => v.IsInDepartment(filterDepartmentId));
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var needle = searchText.Trim();
            views = views.Where(v =>
                v.Username.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || v.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || v.JobTitle.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        return views.OrderBy(v => v.DisplayName).ToList();
    }

    public async Task<MemberProfileView?> GetProfileByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.AppUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), cancellationToken);
        if (user is null) return null;

        var profile = await dbContext.MemberProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.AppUserId == user.Id, cancellationToken);
        var departments = await LoadDepartmentsAsync(profile is null ? [] : [profile], user.Id, cancellationToken);
        return ToView(user, profile, departments.GetValueOrDefault(user.Id) ?? []);
    }

    public async Task<MemberProfileView> SaveProfileAsync(MemberProfileEditorModel editor, string performedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var user = await dbContext.AppUsers.FirstOrDefaultAsync(u => u.Username.ToLower() == editor.Username.ToLower(), cancellationToken)
            ?? throw new InvalidOperationException($"No account named '{editor.Username}' exists.");

        var profile = await dbContext.MemberProfiles.FirstOrDefaultAsync(p => p.AppUserId == user.Id, cancellationToken);
        var isNew = profile is null;
        profile ??= new MemberProfile { AppUserId = user.Id, CreatedBy = performedBy };

        profile.DisplayName = string.IsNullOrWhiteSpace(editor.DisplayName) ? user.Username : editor.DisplayName.Trim();
        profile.AvatarUrl = editor.AvatarUrl?.Trim() ?? string.Empty;
        profile.Bio = editor.Bio?.Trim() ?? string.Empty;
        profile.JobTitle = editor.JobTitle?.Trim() ?? string.Empty;
        profile.DepartmentId = editor.DepartmentId;
        profile.Phone = editor.Phone?.Trim() ?? string.Empty;
        profile.Email = editor.Email?.Trim() ?? string.Empty;
        profile.NotifyUnreadMessagesByEmail = editor.NotifyUnreadMessagesByEmail;
        profile.LinkedInUrl = editor.LinkedInUrl?.Trim() ?? string.Empty;
        profile.TwitterUrl = editor.TwitterUrl?.Trim() ?? string.Empty;
        profile.WebsiteUrl = editor.WebsiteUrl?.Trim() ?? string.Empty;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        profile.UpdatedBy = performedBy;

        if (isNew) dbContext.MemberProfiles.Add(profile);

        // Sync the extra departments: whatever was picked, minus the primary one.
        var wanted = (editor.AdditionalDepartmentIds ?? []).Where(id => id != profile.DepartmentId).ToHashSet();
        var existing = await dbContext.DepartmentMemberships.Where(m => m.AppUserId == user.Id).ToListAsync(cancellationToken);
        dbContext.DepartmentMemberships.RemoveRange(existing.Where(m => !wanted.Contains(m.DepartmentId)));
        var validIds = await dbContext.Departments.AsNoTracking().Where(d => wanted.Contains(d.Id)).Select(d => d.Id).ToListAsync(cancellationToken);
        foreach (var id in validIds.Where(id => existing.All(m => m.DepartmentId != id)))
        {
            dbContext.DepartmentMemberships.Add(new DepartmentMembership { DepartmentId = id, AppUserId = user.Id, CreatedBy = performedBy });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var departments = await LoadDepartmentsAsync([profile], user.Id, cancellationToken);
        return ToView(user, profile, departments.GetValueOrDefault(user.Id) ?? []);
    }

    public async Task<bool> CanEditProfileAsync(string performedByUsername, string targetUsername, CancellationToken cancellationToken = default)
    {
        if (string.Equals(performedByUsername, targetUsername, StringComparison.OrdinalIgnoreCase)) return true;

        var performer = await dbContext.AppUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username.ToLower() == performedByUsername.ToLower(), cancellationToken);
        if (performer is { Role: AppRoles.Admin }) return true;
        if (performer is null) return false;

        var target = await dbContext.AppUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username.ToLower() == targetUsername.ToLower(), cancellationToken);
        if (target is null) return false;

        // A lead of ANY of the target's departments may edit their profile.
        var targetProfile = await dbContext.MemberProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.AppUserId == target.Id, cancellationToken);
        var departments = await LoadDepartmentsAsync(targetProfile is null ? [] : [targetProfile], target.Id, cancellationToken);
        var departmentIds = (departments.GetValueOrDefault(target.Id) ?? []).Select(d => d.Id).ToList();
        if (departmentIds.Count == 0) return false;

        var leads = await dbContext.Departments.AsNoTracking()
            .Where(d => departmentIds.Contains(d.Id) && d.LeadUsername != null)
            .Select(d => d.LeadUsername!)
            .ToListAsync(cancellationToken);
        return leads.Any(lead => string.Equals(lead, performedByUsername, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Dictionary<string, string>> ResolveDisplayNamesAsync(IReadOnlyList<string> usernames, CancellationToken cancellationToken)
    {
        if (usernames.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lowerUsernames = usernames.Select(u => u.ToLowerInvariant()).ToList();
        var users = await dbContext.AppUsers.AsNoTracking()
            .Where(u => lowerUsernames.Contains(u.Username.ToLower()))
            .ToListAsync(cancellationToken);
        var profiles = await dbContext.MemberProfiles.AsNoTracking()
            .Where(p => users.Select(u => u.Id).Contains(p.AppUserId))
            .ToListAsync(cancellationToken);
        var profileByUserId = profiles.ToDictionary(p => p.AppUserId);

        return users.ToDictionary(
            u => u.Username,
            u => profileByUserId.TryGetValue(u.Id, out var p) && !string.IsNullOrWhiteSpace(p.DisplayName) ? p.DisplayName : u.Username,
            StringComparer.OrdinalIgnoreCase);
    }

    // Every department each user belongs to (primary first). userId narrows the membership load
    // to one person; null loads everyone (directory listing).
    private async Task<Dictionary<Guid, List<DepartmentMembershipView>>> LoadDepartmentsAsync(
        IReadOnlyCollection<MemberProfile> profiles, Guid? userId, CancellationToken cancellationToken)
    {
        var membershipQuery = dbContext.DepartmentMemberships.AsNoTracking();
        if (userId is { } onlyUser) membershipQuery = membershipQuery.Where(m => m.AppUserId == onlyUser);
        var memberships = await membershipQuery.ToListAsync(cancellationToken);
        var names = (await dbContext.Departments.AsNoTracking().Select(d => new { d.Id, d.Name }).ToListAsync(cancellationToken))
            .ToDictionary(d => d.Id, d => d.Name);

        var result = new Dictionary<Guid, List<DepartmentMembershipView>>();
        foreach (var profile in profiles.Where(p => p.DepartmentId is { } id && names.ContainsKey(id)))
        {
            result[profile.AppUserId] = [new DepartmentMembershipView(profile.DepartmentId!.Value, names[profile.DepartmentId.Value], true)];
        }
        foreach (var membership in memberships.Where(m => names.ContainsKey(m.DepartmentId)))
        {
            var list = result.TryGetValue(membership.AppUserId, out var existing) ? existing : result[membership.AppUserId] = [];
            if (list.All(d => d.Id != membership.DepartmentId))
                list.Add(new DepartmentMembershipView(membership.DepartmentId, names[membership.DepartmentId], false));
        }

        return result;
    }

    private static MemberProfileView ToView(AppUser user, MemberProfile? profile, IReadOnlyList<DepartmentMembershipView> departments) => new(
        profile?.Id,
        user.Username,
        user.Role,
        user.IsActive,
        string.IsNullOrWhiteSpace(profile?.DisplayName) ? user.Username : profile.DisplayName,
        profile?.AvatarUrl ?? string.Empty,
        profile?.Bio ?? string.Empty,
        profile?.JobTitle ?? string.Empty,
        profile?.DepartmentId,
        departments.FirstOrDefault(d => d.IsPrimary)?.Name,
        profile?.Phone ?? string.Empty,
        profile?.LinkedInUrl ?? string.Empty,
        profile?.TwitterUrl ?? string.Empty,
        profile?.WebsiteUrl ?? string.Empty,
        departments,
        profile?.Email ?? string.Empty,
        profile?.NotifyUnreadMessagesByEmail ?? true);
}
