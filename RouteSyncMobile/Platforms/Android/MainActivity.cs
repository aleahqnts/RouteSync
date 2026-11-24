using Android.App;
using Android.Content.PM;
using Android.OS;
using FleetWiseMobile.Services;

namespace FleetWiseMobile;

// Portrait only. The counter and the checklist are both built as a single column, and
// a phone rotating in a moving bus reflows them for no benefit.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ScreenOrientation = ScreenOrientation.Portrait, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private ConfirmExitCallback? _backCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _ = AskPermissionsOnceAsync();
    }

    // Every runtime permission the app can use, asked one after another on the first launch
    // after install, so the phone is set up before the first shift rather than mid-task.
    // Each later use still checks and asks again, and LocationPrompt covers a grant that
    // was refused or taken back. Installing updates is a settings switch, not a runtime
    // grant, so it stays with the update banner.
    private static async Task AskPermissionsOnceAsync()
    {
        const string Key = "permissions_asked_v1";
        if (Microsoft.Maui.Storage.Preferences.Default.Get(Key, false)) return;
        try
        {
            await Ask<Permissions.PostNotifications>();
            // Background location is only offered once foreground location is granted.
            if (await Ask<Permissions.LocationWhenInUse>() == PermissionStatus.Granted)
                await Ask<Permissions.LocationAlways>();
            await Ask<Permissions.Camera>();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainActivity.Perms] {ex}"); }
        Microsoft.Maui.Storage.Preferences.Default.Set(Key, true);
    }

    private static async Task<PermissionStatus> Ask<T>() where T : Permissions.BasePermission, new()
    {
        var status = await Permissions.CheckStatusAsync<T>();
        return status == PermissionStatus.Granted ? status : await Permissions.RequestAsync<T>();
    }

    // Gallery pick through the phone's own gallery app (ACTION_PICK on the image store).
    // MediaPicker uses Android's system photo picker instead, which looks like Google
    // Photos and is not what a driver expects when asked to choose a photo. The returned
    // address carries a one-time read grant, so no storage permission is needed.
    private const int GalleryRequest = 4211;
    private static TaskCompletionSource<global::Android.Net.Uri?>? _galleryPick;

    private static global::Android.Content.Intent GalleryIntent() =>
        new(global::Android.Content.Intent.ActionPick,
            global::Android.Provider.MediaStore.Images.Media.ExternalContentUri);

    // Whether any app answers a gallery pick. Needs the matching <queries> entry in the
    // manifest, or Android 11 and later hide every answer and this is always false.
    public static bool CanPickFromGallery =>
        Platform.CurrentActivity is MainActivity a &&
        GalleryIntent().ResolveActivity(a.PackageManager!) is not null;

    // Null when the driver backs out without choosing.
    public static async Task<byte[]?> PickFromGalleryAsync()
    {
        if (Platform.CurrentActivity is not MainActivity activity) return null;
        var intent = GalleryIntent();

        _galleryPick?.TrySetResult(null);
        _galleryPick = new TaskCompletionSource<global::Android.Net.Uri?>();
        activity.StartActivityForResult(intent, GalleryRequest);

        var uri = await _galleryPick.Task;
        if (uri is null) return null;
        await using var input = activity.ContentResolver!.OpenInputStream(uri);
        if (input is null) return null;
        await using var copy = new MemoryStream();
        await input.CopyToAsync(copy);
        return copy.ToArray();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, global::Android.Content.Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == GalleryRequest)
            _galleryPick?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }

    protected override void OnResume()
    {
        base.OnResume();
        // Registered on the first resume, after the BlazorWebView has installed its own
        // back handler, so this one takes priority. The press is routed through Blazor by
        // explicit up-target rather than through the WebView's history, which can contain
        // the sign-in page and any redirect that bounced through it.
        if (_backCallback is null)
        {
            _backCallback = new ConfirmExitCallback(this);
            OnBackPressedDispatcher.AddCallback(this, _backCallback);
            // Blazor cannot background an Android task, so the confirmation calls back here.
            BackNavigation.ExitApp = () => MoveTaskToBack(true);
        }
    }

    private sealed class ConfirmExitCallback : AndroidX.Activity.OnBackPressedCallback
    {
        private readonly MainActivity _activity;
        public ConfirmExitCallback(MainActivity activity) : base(true) => _activity = activity;

        public override void HandleOnBackPressed()
        {
            // Navigate up where there is somewhere to go. The prompt is only for the top.
            if (BackNavigation.TryGoBack()) return;

            // The app draws its own confirmation in its own colours. The system dialog
            // below covers the window before the WebView has rendered.
            if (BackNavigation.ExitPrompt?.Invoke() == true) return;

            new AndroidX.AppCompat.App.AlertDialog.Builder(_activity)
                .SetTitle("Exit RouteSync?")!
                .SetMessage("Do you want to close the app?")!
                .SetPositiveButton("Exit", (s, e) => _activity.MoveTaskToBack(true))!
                .SetNegativeButton("Cancel", (s, e) => { })!
                .Show();
        }
    }
}
