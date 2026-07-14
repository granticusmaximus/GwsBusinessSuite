using System.ComponentModel.DataAnnotations;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Users;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class UserManagementService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IPasswordHasher<AppUser> passwordHasher,
    ILogger<UserManagementService> logger,
    ICurrentUserAccessor? currentUserAccessor = null) : IUserManagementService
{
    private readonly ICurrentUserAccessor _currentUserAccessor = currentUserAccessor ?? FixedCurrentUserAccessor.Unknown;

    // A fresh, short-lived DbContext per operation rather than one shared IAppDbContext -
    // this is a long-lived admin-session page where reusing a single tracked context across
    // many operations would accumulate stale/tracked entities over the session's lifetime.
    public async Task<IReadOnlyList<UserView>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AppUsers
            .OrderBy(u => u.Username)
            .Select(u => ToView(u))
            .ToListAsync(cancellationToken);
    }

    public async Task<UserManagementResult> CreateUserAsync(CreateUserInput input, CancellationToken cancellationToken = default)
    {
        var validationError = Validate(input);
        if (validationError is not null)
        {
            return UserManagementResult.Failure(validationError);
        }

        if (!AppRoles.All.Contains(input.Role))
        {
            return UserManagementResult.Failure("Invalid role selected.");
        }

        if (PasswordPolicy.IsWeak(input.Password, input.Username, out var weakReason))
        {
            return UserManagementResult.Failure($"Password {weakReason}.");
        }

        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var performedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);

            var username = input.Username.Trim();
            if (await db.AppUsers.AnyAsync(u => u.Username == username, cancellationToken))
            {
                return UserManagementResult.Failure($"Username '{username}' is already taken.");
            }

            var user = new AppUser
            {
                Username = username,
                Role = input.Role,
                CreatedBy = performedBy
            };
            user.PasswordHash = passwordHasher.HashPassword(user, input.Password);
            db.AppUsers.Add(user);
            await db.SaveChangesAsync(cancellationToken);

            return UserManagementResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create user.");
            return UserManagementResult.Failure($"Unable to create user. {ex.Message}");
        }
    }

    public async Task<UserManagementResult> ChangeRoleAsync(Guid userId, string newRole, CancellationToken cancellationToken = default)
    {
        if (!AppRoles.All.Contains(newRole))
        {
            return UserManagementResult.Failure("Invalid role.");
        }

        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var performedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
            var user = await db.AppUsers.FindAsync([userId], cancellationToken);
            if (user is null)
            {
                return UserManagementResult.Failure("User not found.");
            }

            if (user.Role == AppRoles.Admin && newRole != AppRoles.Admin
                && await IsLastActiveAdminAsync(db, cancellationToken))
            {
                return UserManagementResult.Failure("Cannot change the role of the last Admin account.");
            }

            user.Role = newRole;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            user.UpdatedBy = performedBy;
            await db.SaveChangesAsync(cancellationToken);

            return UserManagementResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update role for user {Id}.", userId);
            return UserManagementResult.Failure($"Unable to update role. {ex.Message}");
        }
    }

    public async Task<UserManagementResult> ResetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var performedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
            var user = await db.AppUsers.FindAsync([userId], cancellationToken);
            if (user is null)
            {
                return UserManagementResult.Failure("User not found.");
            }

            if (PasswordPolicy.IsWeak(newPassword, user.Username, out var weakReason))
            {
                return UserManagementResult.Failure($"Password {weakReason}.");
            }

            user.PasswordHash = passwordHasher.HashPassword(user, newPassword);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            user.UpdatedBy = performedBy;
            await db.SaveChangesAsync(cancellationToken);

            return UserManagementResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset password for user {Id}.", userId);
            return UserManagementResult.Failure($"Unable to reset password. {ex.Message}");
        }
    }

    public async Task<UserManagementResult> ToggleActiveAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var performedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
            var user = await db.AppUsers.FindAsync([userId], cancellationToken);
            if (user is null)
            {
                return UserManagementResult.Failure("User not found.");
            }

            if (user.IsActive && user.Role == AppRoles.Admin && await IsLastActiveAdminAsync(db, cancellationToken))
            {
                return UserManagementResult.Failure("Cannot deactivate the last Admin account.");
            }

            user.IsActive = !user.IsActive;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            user.UpdatedBy = performedBy;
            await db.SaveChangesAsync(cancellationToken);

            return UserManagementResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to toggle active state for user {Id}.", userId);
            return UserManagementResult.Failure($"Unable to update user. {ex.Message}");
        }
    }

    // Queries the database directly rather than relying on a caller-supplied snapshot, so
    // the lockout guard is judged against authoritative data instead of a client's
    // potentially-stale in-memory user list.
    private static async Task<bool> IsLastActiveAdminAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var activeAdminCount = await db.AppUsers.CountAsync(u => u.Role == AppRoles.Admin && u.IsActive, cancellationToken);
        return activeAdminCount <= 1;
    }

    private static string? Validate(CreateUserInput input)
    {
        var context = new ValidationContext(input);
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(input, context, results, validateAllProperties: true)
            ? null
            : results.First().ErrorMessage;
    }

    private static UserView ToView(AppUser user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        Role = user.Role,
        IsActive = user.IsActive,
        CreatedAt = user.CreatedAt
    };
}
