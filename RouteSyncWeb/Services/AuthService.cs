using Microsoft.AspNetCore.Identity;
using FleetWise.Models;

namespace FleetWise.Services
{
    public class AuthService
    {
        // Role 2 is the driver role, which belongs to the mobile app rather than the
        // dashboard. The mobile app's own sign-in admits only that role.
        private const int DriverRoleId = 2;

        private readonly Supabase.Client _supabase;

        public AuthService(Supabase.Client supabase) => _supabase = supabase;

        /// <summary>
        /// Checks a dashboard sign-in, and says which account it was for and why it was
        /// refused.
        /// </summary>
        /// <remarks>
        /// None of this reaches the browser, which is told the same thing whatever the
        /// reason. It goes to the audit trail, where an attempt on a real account has to be
        /// told apart from one on an address that belongs to nobody: only the first can be
        /// broken into, and only the first can be grouped by the account it was aimed at.
        ///
        /// The password is checked before the driver role is. A driver typing their own
        /// correct password here is somebody in the wrong app. A driver's account with the
        /// wrong password is somebody guessing, and checking the role first would record
        /// the two identically.
        /// </remarks>
        public async Task<SignInCheck> CheckSignInAsync(string email, string password)
        {
            // Sign-in is the first call after an idle spell, so it is the one that meets
            // a connection the far end has already closed.
            var usersResponse = await Transient.RunAsync(() => _supabase
                .From<UserModel>()
                .Filter("email_address", Postgrest.Constants.Operator.Equals, email)
                .Get());

            var user = usersResponse.Models.FirstOrDefault();
            if (user is null)
                return SignInCheck.Refused(null, SignInRefusal.NoSuchAccount);
            if (user.PasswordHash is null)
                return SignInCheck.Refused(user.UserId, SignInRefusal.NoPassword);
            if (user.AccountStatus != "Activated")
                return SignInCheck.Refused(user.UserId, SignInRefusal.Inactive);

            var hasher = new PasswordHasher<UserModel>();
            var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
            if (result == PasswordVerificationResult.Failed)
                return SignInCheck.Refused(user.UserId, SignInRefusal.WrongPassword);

            // The dashboard is for operators only. Drivers use the mobile app.
            if (user.RoleId == DriverRoleId)
                return SignInCheck.Refused(user.UserId, SignInRefusal.WrongApp);

            var rolesResponse = await _supabase
                .From<Role>()
                .Filter("role_id", Postgrest.Constants.Operator.Equals, user.RoleId.ToString())
                .Get();

            var role = rolesResponse.Models.FirstOrDefault();
            var roleName = role?.RoleName ?? "Unknown";
            // The dashboard sections this role may see.
            var permissions = role?.WebPermissions?
                .Where(kv => kv.Value).Select(kv => kv.Key).ToList() ?? new List<string>();

            return new SignInCheck(new AuthenticatedUser(
                user.UserId,
                FormatDisplayName(user.FirstName, user.MiddleName, user.LastName),
                user.EmailAddress ?? "",
                roleName,
                permissions), user.UserId, null);
        }

        /// <summary>Hashes and stores a new password, used by the forced first-sign-in
        /// change.</summary>
        public async Task UpdatePasswordAsync(int userId, string newPassword)
        {
            var resp = await _supabase
                .From<UserModel>()
                .Filter("user_id", Postgrest.Constants.Operator.Equals, userId.ToString())
                .Get();

            var user = resp.Models.FirstOrDefault();
            if (user is null) return;

            var hasher = new PasswordHasher<UserModel>();
            user.PasswordHash = hasher.HashPassword(user, newPassword);
            user.UpdatedAt = PhClock.Now;
            await _supabase.From<UserModel>().Update(user);
        }

        private static string FormatDisplayName(string? firstName, string? middleName, string? lastName)
        {
            var middleInitial = string.IsNullOrWhiteSpace(middleName) ? "" : $" {middleName.Trim()[0]}.";
            return $"{firstName}{middleInitial} {lastName}".Trim();
        }
    }

    public record AuthenticatedUser(int UserId, string FullName, string Email, string RoleName, List<string> Permissions);

    /// <summary>Why a dashboard sign-in was refused.</summary>
    public enum SignInRefusal
    {
        NoSuchAccount,
        NoPassword,
        Inactive,
        WrongPassword,

        /// <summary>
        /// A driver, with their correct password. Somebody in the wrong app rather than an
        /// attempt on an account, and kept out of every security rule.
        /// </summary>
        WrongApp,
    }

    /// <summary>The outcome of a dashboard sign-in check.</summary>
    /// <param name="User">Set only when the sign-in succeeded.</param>
    /// <param name="AccountId">
    /// The account the email belongs to, whether or not the sign-in succeeded. Null only
    /// when no account has that email. Recorded against a failure so repeated attempts on
    /// one account can be recognised as such.
    /// </param>
    public record SignInCheck(AuthenticatedUser? User, int? AccountId, SignInRefusal? Refusal)
    {
        public static SignInCheck Refused(int? accountId, SignInRefusal why) => new(null, accountId, why);

        /// <summary>The reason as the audit trail words it, matching the driver app's sign-in.</summary>
        public string? Reason => Refusal switch
        {
            SignInRefusal.NoSuchAccount => "no such account",
            SignInRefusal.NoPassword => "no password set",
            SignInRefusal.Inactive => "account not active",
            SignInRefusal.WrongPassword => "wrong password",
            SignInRefusal.WrongApp => "driver account",
            _ => null,
        };
    }
}
