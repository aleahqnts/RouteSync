using FleetWiseMobile.Models;
using Microsoft.Maui.Storage;

namespace FleetWiseMobile.Services;

// Holds the signed-in driver for the session, and persists a small token, the user
// identifier and JWT, in secure storage so the driver stays signed in across launches.
public class SessionService
{
    private const string UidKey = "fw_uid";
    private const string JwtKey = "fw_jwt";
    private readonly Supabase.Client _supabase;

    public SessionService(Supabase.Client supabase) => _supabase = supabase;

    public UserModel? CurrentUser { get; private set; }
    public bool IsLoggedIn => CurrentUser is not null;

    public string DisplayName
    {
        get
        {
            if (CurrentUser is null) return "";
            var mi = string.IsNullOrWhiteSpace(CurrentUser.MiddleName)
                ? ""
                : $" {CurrentUser.MiddleName!.Trim()[0]}.";
            return $"{CurrentUser.FirstName}{mi} {CurrentUser.LastName}".Trim();
        }
    }

    public async Task SetAsync(UserModel user)
    {
        CurrentUser = user;
        await SecureStorage.Default.SetAsync(UidKey, user.UserId.ToString());

        // Persist the driver JWT, which the authentication service sets on a successful
        // sign-in.
        if (SupabaseConfig.Jwt is not null)
            await SecureStorage.Default.SetAsync(JwtKey, SupabaseConfig.Jwt);
        else
            SecureStorage.Default.Remove(JwtKey);
    }

    // Restores the session from secure storage at launch.
    public async Task<bool> RestoreAsync()
    {
        if (IsLoggedIn) return true;

        var uid = await SecureStorage.Default.GetAsync(UidKey);
        if (string.IsNullOrEmpty(uid)) return false;

        // The saved JWT is reattached before the user is fetched, so that read is itself
        // authenticated. The postgrest header closure takes it from SupabaseConfig.Bearer
        // on every request.
        var jwt = await SecureStorage.Default.GetAsync(JwtKey);
        if (!string.IsNullOrEmpty(jwt))
            SupabaseConfig.Jwt = jwt;

        try
        {
            var resp = await _supabase
                .From<UserModel>()
                .Filter("user_id", Postgrest.Constants.Operator.Equals, uid)
                .Get();
            CurrentUser = resp.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            // An expired or revoked JWT makes the request fail outright. Treat that as
            // signed out and clear the stale token.
            System.Diagnostics.Debug.WriteLine($"[Session.Restore] {ex.Message}");
            ClearJwt();
            return false;
        }
        return IsLoggedIn;
    }

    public void Logout()
    {
        CurrentUser = null;
        SecureStorage.Default.Remove(UidKey);
        ClearJwt();
    }

    private void ClearJwt()
    {
        SupabaseConfig.Jwt = null; // Bearer falls back to the anon key everywhere
        SecureStorage.Default.Remove(JwtKey);
    }
}
