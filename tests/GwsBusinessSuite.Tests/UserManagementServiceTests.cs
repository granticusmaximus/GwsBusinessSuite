using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Users;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class UserManagementServiceTests
{
    [Fact]
    public async Task CreateUserAsync_ShouldSucceed_ForValidInput()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);

        var result = await service.CreateUserAsync(new CreateUserInput
        {
            Username = "jsmith",
            Password = "correct-horse",
            Role = AppRoles.Author
        });

        result.Succeeded.Should().BeTrue();
        var users = await service.ListUsersAsync();
        users.Should().ContainSingle(u => u.Username == "jsmith" && u.Role == AppRoles.Author);
        await using var db = CreateReadDbContext(connection);
        (await db.AppUsers.SingleAsync(u => u.Username == "jsmith")).CreatedBy.Should().Be("grantwatson");
    }

    [Fact]
    public async Task CreateUserAsync_ShouldFail_ForDuplicateUsername()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);
        await service.CreateUserAsync(new CreateUserInput { Username = "jsmith", Password = "correct-horse", Role = AppRoles.Author });

        var result = await service.CreateUserAsync(new CreateUserInput { Username = "jsmith", Password = "another-password", Role = AppRoles.Author });

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("already taken");
        (await service.ListUsersAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateUserAsync_ShouldFail_ForPasswordShorterThanEightCharacters()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);

        var result = await service.CreateUserAsync(new CreateUserInput
        {
            Username = "jsmith",
            Password = "short1",
            Role = AppRoles.Author
        });

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("8 characters");
        (await service.ListUsersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateUserAsync_ShouldFail_ForInvalidRole()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);

        var result = await service.CreateUserAsync(new CreateUserInput
        {
            Username = "jsmith",
            Password = "correct-horse",
            Role = "SuperUser"
        });

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("Invalid role");
    }

    [Fact]
    public async Task ChangeRoleAsync_ShouldUpdateRole_ForAValidWhitelistedRole()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);
        await service.CreateUserAsync(new CreateUserInput { Username = "author1", Password = "correct-horse", Role = AppRoles.Author });
        var userId = (await service.ListUsersAsync()).Single().Id;

        var result = await service.ChangeRoleAsync(userId, AppRoles.Contributor);

        result.Succeeded.Should().BeTrue();
        (await service.ListUsersAsync()).Single().Role.Should().Be(AppRoles.Contributor);
        await using var db = CreateReadDbContext(connection);
        (await db.AppUsers.SingleAsync(u => u.Id == userId)).UpdatedBy.Should().Be("grantwatson");
    }

    [Fact]
    public async Task ChangeRoleAsync_ShouldFail_ForABogusRole()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);
        await service.CreateUserAsync(new CreateUserInput { Username = "author1", Password = "correct-horse", Role = AppRoles.Author });
        var userId = (await service.ListUsersAsync()).Single().Id;

        var result = await service.ChangeRoleAsync(userId, "SuperUser");

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("Invalid role");
    }

    [Fact]
    public async Task ResetPasswordAsync_ShouldSucceed_ForValidPassword()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);
        await service.CreateUserAsync(new CreateUserInput { Username = "jsmith", Password = "correct-horse", Role = AppRoles.Author });
        var userId = (await service.ListUsersAsync()).Single().Id;

        var result = await service.ResetPasswordAsync(userId, "new-correct-horse");

        result.Succeeded.Should().BeTrue();
        await using var db = CreateReadDbContext(connection);
        (await db.AppUsers.SingleAsync(u => u.Id == userId)).UpdatedBy.Should().Be("grantwatson");
    }

    [Fact]
    public async Task ResetPasswordAsync_ShouldFail_ForPasswordShorterThanEightCharacters()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);
        await service.CreateUserAsync(new CreateUserInput { Username = "jsmith", Password = "correct-horse", Role = AppRoles.Author });
        var userId = (await service.ListUsersAsync()).Single().Id;

        var result = await service.ResetPasswordAsync(userId, "short1");

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("8 characters");
    }

    [Fact]
    public async Task ToggleActiveAsync_ShouldFlipActiveState_ForANonAdminOrNonLastAdminUser()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);
        await service.CreateUserAsync(new CreateUserInput { Username = "author1", Password = "correct-horse", Role = AppRoles.Author });
        var userId = (await service.ListUsersAsync()).Single().Id;

        var deactivateResult = await service.ToggleActiveAsync(userId);
        deactivateResult.Succeeded.Should().BeTrue();
        (await service.ListUsersAsync()).Single().IsActive.Should().BeFalse();
        await using var db = CreateReadDbContext(connection);
        (await db.AppUsers.SingleAsync(u => u.Id == userId)).UpdatedBy.Should().Be("grantwatson");

        var reactivateResult = await service.ToggleActiveAsync(userId);
        reactivateResult.Succeeded.Should().BeTrue();
        (await service.ListUsersAsync()).Single().IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleActiveAsync_ShouldReject_DeactivatingTheSoleRemainingAdmin()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);
        await service.CreateUserAsync(new CreateUserInput { Username = "admin1", Password = "correct-horse", Role = AppRoles.Admin });
        var adminId = (await service.ListUsersAsync()).Single().Id;

        var result = await service.ToggleActiveAsync(adminId);

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("last Admin");
        (await service.ListUsersAsync()).Single().IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ChangeRoleAsync_ShouldReject_DemotingTheSoleRemainingAdmin()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);
        await service.CreateUserAsync(new CreateUserInput { Username = "admin1", Password = "correct-horse", Role = AppRoles.Admin });
        var adminId = (await service.ListUsersAsync()).Single().Id;

        var result = await service.ChangeRoleAsync(adminId, AppRoles.Author);

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("last Admin");
        (await service.ListUsersAsync()).Single().Role.Should().Be(AppRoles.Admin);
    }

    [Fact]
    public async Task ToggleActiveAsync_ShouldAllowDeactivatingAnAdmin_WhenAnotherActiveAdminExists()
    {
        using var connection = await OpenConnectionAsync();
        var service = CreateService(connection);
        await service.CreateUserAsync(new CreateUserInput { Username = "admin1", Password = "correct-horse", Role = AppRoles.Admin });
        await service.CreateUserAsync(new CreateUserInput { Username = "admin2", Password = "correct-horse", Role = AppRoles.Admin });
        var firstAdminId = (await service.ListUsersAsync()).First(u => u.Username == "admin1").Id;

        var result = await service.ToggleActiveAsync(firstAdminId);

        result.Succeeded.Should().BeTrue();
    }

    private static UserManagementService CreateService(SqliteConnection connection) =>
        new(
            new TestDbContextFactory(connection),
            new PasswordHasher<AppUser>(),
            NullLogger<UserManagementService>.Instance,
            new FixedCurrentUserAccessor("grantwatson"));

    private static ApplicationDbContext CreateReadDbContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        return connection;
    }

    // UserManagementService deliberately takes IDbContextFactory (fresh short-lived
    // contexts per operation) rather than a single shared context - this fake mirrors that
    // by handing out a new ApplicationDbContext per call, all sharing one open in-memory
    // SQLite connection so data persists across them.
    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
