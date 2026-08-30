using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Users;
using GwsBusinessSuite.Application.SecurityAudit;
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
    ICurrentUserAccessor? currentUserAccessor = null,
    ISecretProtector? secretProtector = null,
    TimeProvider? timeProvider = null,
    ISecurityAuditService? securityAuditService = null) : IUserManagementService
{
    private readonly ICurrentUserAccessor _currentUserAccessor = currentUserAccessor ?? FixedCurrentUserAccessor.Unknown;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private ISecretProtector SecretProtector => secretProtector
        ?? throw new InvalidOperationException("MFA secret protection is not configured.");

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

            if (securityAuditService is not null)
            {
                await securityAuditService.RecordAsync(new SecurityAuditInput(
                    SecurityAuditCategories.AccountAdministration, "UserCreated", SecurityAuditOutcomes.Succeeded,
                    SecurityAuditSeverities.High, "AppUser", user.Id.ToString(),
                    new Dictionary<string, string?> { ["role"] = user.Role, ["username"] = user.Username }), cancellationToken);
            }

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

            var priorRole = user.Role;
            user.Role = newRole;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            user.UpdatedBy = performedBy;
            await db.SaveChangesAsync(cancellationToken);

            if (securityAuditService is not null)
            {
                await securityAuditService.RecordAsync(new SecurityAuditInput(
                    SecurityAuditCategories.AccountAdministration, "UserRoleChanged", SecurityAuditOutcomes.Succeeded,
                    SecurityAuditSeverities.High, "AppUser", user.Id.ToString(),
                    new Dictionary<string, string?> { ["fromRole"] = priorRole, ["toRole"] = newRole, ["username"] = user.Username }), cancellationToken);
            }

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
            // An admin actively resetting the password is a deliberate account-recovery
            // action - waiting out a stale lockout timer on top of that would just be
            // confusing, so clear it here rather than requiring a separate Unlock click.
            user.FailedLoginAttempts = 0;
            user.LockoutEndAt = null;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            user.UpdatedBy = performedBy;
            await db.SaveChangesAsync(cancellationToken);

            if (securityAuditService is not null)
            {
                await securityAuditService.RecordAsync(new SecurityAuditInput(
                    SecurityAuditCategories.AccountAdministration, "PasswordReset", SecurityAuditOutcomes.Succeeded,
                    SecurityAuditSeverities.High, "AppUser", user.Id.ToString(),
                    new Dictionary<string, string?> { ["username"] = user.Username }), cancellationToken);
            }

            return UserManagementResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset password for user {Id}.", userId);
            return UserManagementResult.Failure($"Unable to reset password. {ex.Message}");
        }
    }

    public async Task<LoginAttemptResult> AttemptLoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var trimmedUsername = (username ?? string.Empty).Trim();
        var user = await db.AppUsers
            .FirstOrDefaultAsync(u => u.Username == trimmedUsername && u.IsActive, cancellationToken);

        if (user is null)
        {
            return new LoginAttemptResult(false, null, false, null);
        }

        var now = _timeProvider.GetUtcNow();
        if (user.LockoutEndAt is { } lockoutEnd && lockoutEnd > now)
        {
            // Reject before hashing the candidate password at all while locked out - no
            // point paying the hashing cost, and it keeps the lockout check from being a
            // timing oracle for whether the password would otherwise have been correct.
            return new LoginAttemptResult(false, null, true, lockoutEnd - now);
        }

        var verifyResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty);
        if (verifyResult == PasswordVerificationResult.Failed)
        {
            user.FailedLoginAttempts += 1;
            var justLockedOut = user.FailedLoginAttempts >= LoginLockoutPolicy.MaxFailedAttempts;
            if (justLockedOut)
            {
                user.LockoutEndAt = now.Add(LoginLockoutPolicy.LockoutDuration);
                user.FailedLoginAttempts = 0;
            }

            await db.SaveChangesAsync(cancellationToken);
            return new LoginAttemptResult(false, null, justLockedOut, justLockedOut ? LoginLockoutPolicy.LockoutDuration : null);
        }

        user.FailedLoginAttempts = 0;
        user.LockoutEndAt = null;
        await db.SaveChangesAsync(cancellationToken);

        return new LoginAttemptResult(true, ToView(user), false, null);
    }

    public async Task<LoginAttemptResult?> AttemptDeviceLoginAsync(
        string providedSecret, string configuredSecret, string username, string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuredSecret) || string.IsNullOrEmpty(providedSecret)
            || providedSecret.Length != configuredSecret.Length
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(providedSecret), Encoding.UTF8.GetBytes(configuredSecret)))
        {
            return null;
        }

        return await AttemptLoginAsync(username, password, cancellationToken);
    }

    public async Task<MfaEnrollment?> PrepareMfaEnrollmentAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (user is null || user.MfaEnabled)
        {
            return null;
        }

        string secret;
        if (string.IsNullOrWhiteSpace(user.MfaSecretProtected))
        {
            secret = Base32Encode(RandomNumberGenerator.GetBytes(20));
            user.MfaSecretProtected = SecretProtector.Protect(secret);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            secret = SecretProtector.Unprotect(user.MfaSecretProtected);
        }

        var issuer = Uri.EscapeDataString("GWS Business Suite");
        var account = Uri.EscapeDataString(user.Username);
        return new MfaEnrollment(
            secret,
            $"otpauth://totp/{issuer}:{account}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30");
    }

    public async Task<MfaEnrollmentResult> CompleteMfaEnrollmentAsync(
        Guid userId,
        string code,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (user is null || user.MfaEnabled || string.IsNullOrWhiteSpace(user.MfaSecretProtected))
        {
            return new MfaEnrollmentResult(false, null, [], "MFA enrollment is not available.");
        }

        var secret = SecretProtector.Unprotect(user.MfaSecretProtected);
        var acceptedStep = FindAcceptedTotpStep(secret, code, _timeProvider.GetUtcNow());
        if (acceptedStep is null)
        {
            return new MfaEnrollmentResult(false, null, [], "The authenticator code is invalid or expired.");
        }

        var recoveryCodes = Enumerable.Range(0, 10)
            .Select(_ => FormatRecoveryCode(Base32Encode(RandomNumberGenerator.GetBytes(10))))
            .ToArray();
        user.MfaRecoveryCodeHashesJson = JsonSerializer.Serialize(recoveryCodes.Select(HashRecoveryCode));
        user.MfaEnabled = true;
        user.MfaEnrolledAt = _timeProvider.GetUtcNow();
        user.MfaLastAcceptedStep = acceptedStep;
        user.FailedLoginAttempts = 0;
        user.LockoutEndAt = null;
        await db.SaveChangesAsync(cancellationToken);

        return new MfaEnrollmentResult(true, ToView(user), recoveryCodes);
    }

    public async Task<MfaVerificationResult> VerifyMfaAsync(
        Guid userId,
        string code,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (user is null || !user.MfaEnabled || string.IsNullOrWhiteSpace(user.MfaSecretProtected))
        {
            return new MfaVerificationResult(false, null, FailureReason: "MFA is not configured for this account.");
        }

        var normalizedCode = NormalizeCode(code);
        var secret = SecretProtector.Unprotect(user.MfaSecretProtected);
        var acceptedStep = FindAcceptedTotpStep(secret, normalizedCode, _timeProvider.GetUtcNow());
        if (acceptedStep is not null && (user.MfaLastAcceptedStep is null || acceptedStep > user.MfaLastAcceptedStep))
        {
            user.MfaLastAcceptedStep = acceptedStep;
            await db.SaveChangesAsync(cancellationToken);
            return new MfaVerificationResult(true, ToView(user));
        }

        var hashes = JsonSerializer.Deserialize<List<string>>(user.MfaRecoveryCodeHashesJson) ?? [];
        var recoveryHash = HashRecoveryCode(normalizedCode);
        var recoveryIndex = hashes.FindIndex(hash => CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(hash),
            Convert.FromHexString(recoveryHash)));
        if (recoveryIndex >= 0)
        {
            hashes.RemoveAt(recoveryIndex);
            user.MfaRecoveryCodeHashesJson = JsonSerializer.Serialize(hashes);
            await db.SaveChangesAsync(cancellationToken);
            return new MfaVerificationResult(true, ToView(user), true);
        }

        return new MfaVerificationResult(false, null, FailureReason: "The authenticator or recovery code is invalid.");
    }

    public async Task<UserManagementResult> UnlockUserAsync(Guid userId, CancellationToken cancellationToken = default)
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

            user.FailedLoginAttempts = 0;
            user.LockoutEndAt = null;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            user.UpdatedBy = performedBy;
            await db.SaveChangesAsync(cancellationToken);

            if (securityAuditService is not null)
            {
                await securityAuditService.RecordAsync(new SecurityAuditInput(
                    SecurityAuditCategories.AccountAdministration, "AccountUnlocked", SecurityAuditOutcomes.Succeeded,
                    SecurityAuditSeverities.High, "AppUser", user.Id.ToString(),
                    new Dictionary<string, string?> { ["username"] = user.Username }), cancellationToken);
            }

            return UserManagementResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to unlock user {Id}.", userId);
            return UserManagementResult.Failure($"Unable to unlock user. {ex.Message}");
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

            if (securityAuditService is not null)
            {
                await securityAuditService.RecordAsync(new SecurityAuditInput(
                    SecurityAuditCategories.AccountAdministration,
                    user.IsActive ? "UserActivated" : "UserDeactivated",
                    SecurityAuditOutcomes.Succeeded, SecurityAuditSeverities.High,
                    "AppUser", user.Id.ToString(),
                    new Dictionary<string, string?> { ["username"] = user.Username }), cancellationToken);
            }

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
        CreatedAt = user.CreatedAt,
        LockoutEndAt = user.LockoutEndAt,
        MfaEnabled = user.MfaEnabled
    };

    private static long? FindAcceptedTotpStep(string base32Secret, string? submittedCode, DateTimeOffset now)
    {
        var normalized = NormalizeCode(submittedCode);
        if (normalized.Length != 6 || !normalized.All(char.IsAsciiDigit)) return null;

        var secret = Base32Decode(base32Secret);
        var currentStep = now.ToUnixTimeSeconds() / 30;
        for (var offset = -1; offset <= 1; offset++)
        {
            var step = currentStep + offset;
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(GenerateTotp(secret, step)),
                    Encoding.ASCII.GetBytes(normalized)))
            {
                return step;
            }
        }

        return null;
    }

    private static string GenerateTotp(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24)
                    | (hash[offset + 1] << 16)
                    | (hash[offset + 2] << 8)
                    | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static string NormalizeCode(string? code) =>
        new((code ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string FormatRecoveryCode(string value) => $"{value[..8]}-{value[8..16]}";

    private static string HashRecoveryCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCode(code))));

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    private static byte[] Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var character in NormalizeCode(value))
        {
            var index = alphabet.IndexOf(character);
            if (index < 0) throw new FormatException("Invalid MFA secret.");
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 255));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
