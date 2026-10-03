using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using FleetWiseMobile.Services;

namespace FleetWiseMobile.Platforms.Android;

/// <summary>
/// System-tray notification for new messages, on its own channel so it can be managed
/// separately from the trip-tracking service's notification.
/// </summary>
public class AndroidLocalNotifier : ILocalNotifier
{
    private const string ChannelId = "routesync_messages";

    public void Show(int id, string title, string body)
    {
        var ctx = global::Android.App.Application.Context;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var mgr = (NotificationManager)ctx.GetSystemService(Context.NotificationService)!;
            var channel = new NotificationChannel(ChannelId, "Messages", NotificationImportance.High);
            mgr.CreateNotificationChannel(channel);
        }

        // Tapping the notification opens the app.
        var launch = ctx.PackageManager?.GetLaunchIntentForPackage(ctx.PackageName!);
        launch?.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pending = PendingIntent.GetActivity(ctx, 0, launch,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var notif = new NotificationCompat.Builder(ctx, ChannelId)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetColor(unchecked((int)0xFF3AB39C))
            .SetPriority(NotificationCompat.PriorityHigh)
            .SetAutoCancel(true)
            .SetContentIntent(pending)
            .Build();

        NotificationManagerCompat.From(ctx).Notify(id, notif);
    }
}
