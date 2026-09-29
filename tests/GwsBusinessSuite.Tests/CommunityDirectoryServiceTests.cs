using FluentAssertions;
using GwsBusinessSuite.Application.Community;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class CommunityDirectoryServiceTests
{
    [Fact]
    public async Task GetProfileByUsernameAsync_ShouldReturnSensibleDefaults_ForAUserWithNoProfileYet()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = fixture.AddUser("jdoe");
        await fixture.Db.SaveChangesAsync();

        var profile = await fixture.Service.GetProfileByUsernameAsync("jdoe");

        profile.Should().NotBeNull();
        profile!.ProfileId.Should().BeNull();
        profile.DisplayName.Should().Be("jdoe");
        profile.Bio.Should().BeEmpty();
    }

    [Fact]
    public async Task GetProfileByUsernameAsync_ShouldReturnNull_ForAnUnknownUsername()
    {
        await using var fixture = await Fixture.CreateAsync();

        var profile = await fixture.Service.GetProfileByUsernameAsync("nobody");

        profile.Should().BeNull();
    }

    [Fact]
    public async Task SaveProfileAsync_ShouldCreateThenUpdateTheSameProfileRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddUser("jdoe");
        await fixture.Db.SaveChangesAsync();

        var created = await fixture.Service.SaveProfileAsync(new MemberProfileEditorModel
        {
            Username = "jdoe",
            DisplayName = "Jane Doe",
            JobTitle = "Engineer"
        }, "jdoe");

        created.DisplayName.Should().Be("Jane Doe");
        created.JobTitle.Should().Be("Engineer");

        var updated = await fixture.Service.SaveProfileAsync(new MemberProfileEditorModel
        {
            Username = "jdoe",
            DisplayName = "Jane Doe",
            JobTitle = "Senior Engineer"
        }, "jdoe");

        updated.ProfileId.Should().Be(created.ProfileId, "saving twice should update the same row, not create a second one");
        updated.JobTitle.Should().Be("Senior Engineer");
        (await fixture.Db.MemberProfiles.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SaveProfileAsync_ShouldThrow_ForAnUnknownUsername()
    {
        await using var fixture = await Fixture.CreateAsync();

        var action = async () => await fixture.Service.SaveProfileAsync(new MemberProfileEditorModel { Username = "ghost" }, "admin");

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListProfilesAsync_ShouldFilterByDepartmentAndSearchText()
    {
        await using var fixture = await Fixture.CreateAsync();
        var eng = fixture.AddUser("eng-lead");
        var sales = fixture.AddUser("sales-rep");
        await fixture.Db.SaveChangesAsync();

        var engineering = await fixture.Service.SaveDepartmentAsync(new DepartmentEditorModel { Name = "Engineering" });
        await fixture.Service.SaveProfileAsync(new MemberProfileEditorModel
        {
            Username = "eng-lead", DisplayName = "Alex Rivera", DepartmentId = engineering.Id, JobTitle = "Tech Lead"
        }, "admin");
        await fixture.Service.SaveProfileAsync(new MemberProfileEditorModel
        {
            Username = "sales-rep", DisplayName = "Jordan Lee", JobTitle = "Account Executive"
        }, "admin");

        var byDepartment = await fixture.Service.ListProfilesAsync(departmentId: engineering.Id);
        byDepartment.Select(p => p.Username).Should().ContainSingle().Which.Should().Be("eng-lead");

        var bySearch = await fixture.Service.ListProfilesAsync(searchText: "Jordan");
        bySearch.Select(p => p.Username).Should().ContainSingle().Which.Should().Be("sales-rep");

        var all = await fixture.Service.ListProfilesAsync();
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListProfilesAsync_ShouldExcludeInactiveAccounts()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddUser("active-user");
        var inactive = fixture.AddUser("inactive-user");
        inactive.IsActive = false;
        await fixture.Db.SaveChangesAsync();

        var profiles = await fixture.Service.ListProfilesAsync();

        profiles.Select(p => p.Username).Should().ContainSingle().Which.Should().Be("active-user");
    }

    [Fact]
    public async Task DeleteDepartmentAsync_ShouldOrphanItsMembers_NotDeleteThem()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddUser("member");
        await fixture.Db.SaveChangesAsync();
        var department = await fixture.Service.SaveDepartmentAsync(new DepartmentEditorModel { Name = "Temp Dept" });
        await fixture.Service.SaveProfileAsync(new MemberProfileEditorModel { Username = "member", DepartmentId = department.Id }, "admin");

        await fixture.Service.DeleteDepartmentAsync(department.Id);

        var profile = await fixture.Service.GetProfileByUsernameAsync("member");
        profile!.DepartmentId.Should().BeNull();
        (await fixture.Service.ListDepartmentsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CanEditProfileAsync_ShouldAllowSelfAdminAndDepartmentLead_ButNoOneElse()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admin = fixture.AddUser("the-admin");
        admin.Role = AppRoles.Admin;
        fixture.AddUser("lead");
        fixture.AddUser("member");
        fixture.AddUser("stranger");
        await fixture.Db.SaveChangesAsync();

        var department = await fixture.Service.SaveDepartmentAsync(new DepartmentEditorModel { Name = "Ops", LeadUsername = "lead" });
        await fixture.Service.SaveProfileAsync(new MemberProfileEditorModel { Username = "member", DepartmentId = department.Id }, "admin");

        (await fixture.Service.CanEditProfileAsync("member", "member")).Should().BeTrue("a user can always edit their own profile");
        (await fixture.Service.CanEditProfileAsync("the-admin", "member")).Should().BeTrue("an Admin can edit any profile");
        (await fixture.Service.CanEditProfileAsync("lead", "member")).Should().BeTrue("a department lead can edit their own department's members");
        (await fixture.Service.CanEditProfileAsync("stranger", "member")).Should().BeFalse("an unrelated staff member should not be able to edit someone else's profile");
    }

    private sealed class Fixture(SqliteConnection connection, ApplicationDbContext db) : IAsyncDisposable
    {
        public ApplicationDbContext Db { get; } = db;
        public CommunityDirectoryService Service { get; } = new(db);

        public AppUser AddUser(string username)
        {
            var user = new AppUser { Username = username };
            Db.AppUsers.Add(user);
            return user;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
