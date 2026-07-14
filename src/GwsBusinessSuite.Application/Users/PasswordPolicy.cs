namespace GwsBusinessSuite.Application.Users;

// A single password policy shared by every path that sets a password (initial admin
// seed in Program.cs, CreateUserAsync, ResetPasswordAsync) - previously the seed path
// alone required 12+ characters plus a username/common-password check, while
// CreateUserAsync/ResetPasswordAsync only required 8 characters with no other checks.
// Length + a common-password blocklist + not-matching-the-username follows NIST 800-63B's
// guidance (favor length over forced complexity classes like "must contain a symbol").
public static class PasswordPolicy
{
    public const int MinLength = 12;

    private static readonly string[] CommonWeakPasswords =
    [
        "password", "admin", "administrator", "changeme", "letmein",
        "12345678", "123456789", "qwertyuiop", "password123"
    ];

    public static bool IsWeak(string password, string? username, out string reason)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            reason = $"must be at least {MinLength} characters";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(username) && string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
        {
            reason = "must not be the same as the username";
            return true;
        }

        if (CommonWeakPasswords.Contains(password, StringComparer.OrdinalIgnoreCase))
        {
            reason = "is a commonly guessed password";
            return true;
        }

        reason = string.Empty;
        return false;
    }
}
