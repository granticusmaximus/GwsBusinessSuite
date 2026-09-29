namespace GwsBusinessSuite.Application.Community;

public interface ICommunityDirectoryService
{
    Task<IReadOnlyList<DepartmentView>> ListDepartmentsAsync(CancellationToken cancellationToken = default);
    Task<DepartmentView> SaveDepartmentAsync(DepartmentEditorModel editor, CancellationToken cancellationToken = default);
    Task DeleteDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default);

    // departmentId=null lists every active staff account; searchText matches against username,
    // display name, or job title (case-insensitive, substring).
    Task<IReadOnlyList<MemberProfileView>> ListProfilesAsync(
        Guid? departmentId = null, string? searchText = null, CancellationToken cancellationToken = default);

    // Never returns null for an existing AppUser, even one with no MemberProfile row yet - see
    // MemberProfileView's own doc comment for the lazily-materialized default shape. Returns
    // null only when no such AppUser exists at all.
    Task<MemberProfileView?> GetProfileByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<MemberProfileView> SaveProfileAsync(MemberProfileEditorModel editor, string performedBy, CancellationToken cancellationToken = default);

    // Workstream E, Phase 3 (department-scoped permission) - true when performedByUsername is an
    // Admin, is targetUsername itself, or is the LeadUsername of targetUsername's own department.
    Task<bool> CanEditProfileAsync(string performedByUsername, string targetUsername, CancellationToken cancellationToken = default);
}
