namespace FleetWise.Services
{
    // What counts as an acceptable password, and the temporary one issued to new accounts.
    //
    // A sign-in using exactly the temporary value proves the account is still on it, so
    // no column tracks whether the password has been changed. The change form rejects
    // that value, so it cannot be kept.
    //
    // These rules are the browser-side half. _shared/password.ts applies the same policy
    // server-side and is the half that holds. Keep the two in step.
    //
    // Symbols are allowed, not required: length carries the strength, and demanding one
    // yields predictable endings and awkward phone typing.
    public static class PasswordPolicy
    {
        public const string TemporaryPassword = "@Temp123";

        // Stamped on the authentication cookie while the password still has to be changed.
        public const string MustChangeClaim = "pwd_temp";

        public const int MinLength = 8;

        // Not a strength rule. Hashing is PBKDF2 at 100k iterations, so an unbounded
        // password would let anyone spend the server's CPU at will.
        public const int MaxLength = 128;

        // One lowercase letter, one uppercase letter, one digit, in any order. Written as
        // lookaheads so a single attribute covers all three and still works client-side.
        public const string ComplexityPattern = @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).*$";

        // Messages are literals rather than interpolations because attribute arguments
        // must be compile-time constants. They restate MinLength, so change both together.
        public const string LengthMessage = "Password must be at least 8 characters.";
        public const string ComplexityMessage =
            "Password needs an uppercase letter, a lowercase letter and a number.";

        // The rule in one line, for hint text above a password field.
        public const string Rule = "8+ characters, with upper case, lower case and a number.";
    }
}
