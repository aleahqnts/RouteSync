namespace FleetWiseMobile.Services;

/// <summary>
/// Bridge between the Android back button and Blazor's routing.
/// </summary>
/// <remarks>
/// A hardware back press arrives at the Android activity, which has no knowledge of which
/// Blazor page is on screen. This class lets the page decide first, and leaves the exit
/// prompt for when there is nowhere further up.
///
/// The WebView's own history is deliberately not used. It includes the sign-in page and
/// any redirect that bounced through it, so going back could land a signed-in driver on
/// the sign-in screen or a page their session no longer permits. An explicit up-target
/// per route cannot do that.
/// </remarks>
public static class BackNavigation
{
    /// <summary>
    /// Set while a signed-in page is on screen. Returns true if the press was handled by
    /// navigating, and false when there is nowhere up and the caller should offer to exit.
    /// </summary>
    public static Func<bool>? AppHandler { get; set; }

    /// <summary>
    /// Set by a page that owns its own back behaviour, such as a form with steps where
    /// back means the previous step. Takes priority over <see cref="AppHandler"/>.
    /// </summary>
    /// <remarks>The page must clear this on dispose, or the handler outlives the page.</remarks>
    public static Func<bool>? PageHandler { get; set; }

    /// <summary>
    /// Shows the app's own exit confirmation, returning true if it took the press.
    /// </summary>
    /// <remarks>
    /// The activity falls back to a system dialog only while this is null, which is the
    /// window before the WebView has rendered anything.
    /// </remarks>
    public static Func<bool>? ExitPrompt { get; set; }

    /// <summary>
    /// Sends the app to the background, called by the exit confirmation. Set by the
    /// activity, because Blazor cannot background an Android task on its own.
    /// </summary>
    public static Action? ExitApp { get; set; }

    public static bool TryGoBack() =>
        (PageHandler?.Invoke() ?? false) || (AppHandler?.Invoke() ?? false);

    /// <summary>
    /// Where each route goes when the driver presses back, or null when the route is
    /// already at the top and the exit prompt is the correct response.
    /// </summary>
    /// <remarks>
    /// Mirrors the back arrows the pages already draw, so the hardware button and the
    /// on-screen control agree.
    /// </remarks>
    public static string? UpFrom(string path)
    {
        // Trailing id segment, for the routes that carry a trip.
        var id = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

        if (path.StartsWith("/trip-details/", StringComparison.Ordinal)) return "/home";
        if (path.StartsWith("/trip-report/", StringComparison.Ordinal)) return "/trips";
        if (path.StartsWith("/trip-active/", StringComparison.Ordinal)) return "/home";
        // The checklist log is reached from the home screen and from a trip report, so it
        // has no single parent. The page registers its own handler to step back through
        // history; this is only the fallback if that is not in place, and it deliberately
        // never points at the counting screen.
        if (path.StartsWith("/checklist-log/", StringComparison.Ordinal)) return "/home";
        if (path.StartsWith("/camera-calibrate/", StringComparison.Ordinal)) return $"/trip-active/{id}";
        if (path.StartsWith("/checklist/", StringComparison.Ordinal)) return "/home";

        return path switch
        {
            "/leave" => "/profile",
            "/trips" or "/notifications" or "/profile" => "/home",
            _ => null, // /home, /, /set-password: nothing above them
        };
    }
}
