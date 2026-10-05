namespace FleetWiseMobile;

/// <summary>
/// Supabase connection settings.
/// </summary>
/// <remarks>
/// The key here is the publishable one, which is intended to ship inside client apps. The
/// service key must never be placed here: it bypasses row-level security entirely.
/// </remarks>
public static class SupabaseConfig
{
    public const string Url = "https://ggrhmjiamyqhihtyfnui.supabase.co";
    public const string Key = "sb_publishable_m47oBT9uYiiOSOrADi2Clw_t-yFGWlO";
    public const string FunctionsUrl = $"{Url}/functions/v1";

    // Driver JWT issued by the auth-login edge function. Null until sign-in succeeds.
    public static string? Jwt { get; set; }

    // Sent as the authorization bearer on every request: the driver JWT once there is
    // one, and the publishable key before that.
    public static string Bearer => Jwt ?? Key;
}
