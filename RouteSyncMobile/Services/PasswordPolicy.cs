namespace FleetWiseMobile.Services;

// What counts as an acceptable password on the driver app.
//
// This is the on-screen half only, so a driver learns the rule while typing rather
// than after a round trip. The change-password and password-reset-complete edge
// functions apply the same policy server-side, which is the half that actually
// holds. Keep the two in step.
//
// A special character is allowed but not required. Length carries most of the
// strength, and demanding a symbol pushes people towards predictable endings while
// being awkward on the phone keyboards this app runs on.
public static class PasswordPolicy
{
    public const int MinLength = 8;

    // Not a strength rule. Hashing is PBKDF2 at 100k iterations, so an unbounded
    // password would let anyone spend the server's CPU at will.
    public const int MaxLength = 128;

    // The rule in one line, for hint text under a password field.
    public const string Rule = "8+ characters, with upper case, lower case and a number.";

    // null when the password is acceptable, otherwise what to tell the driver.
    public static string? Problem(string pwd)
    {
        if (pwd.Length < MinLength) return $"Password must be at least {MinLength} characters.";
        if (pwd.Length > MaxLength) return $"Password must be {MaxLength} characters or fewer.";
        if (!pwd.Any(char.IsLower)) return "Password needs a lowercase letter.";
        if (!pwd.Any(char.IsUpper)) return "Password needs an uppercase letter.";
        if (!pwd.Any(char.IsDigit)) return "Password needs a number.";
        return null;
    }
}
