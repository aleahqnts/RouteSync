package com.routesync.cameracount

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.routesync.cameracount.data.Prefs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

/**
 * Reopens the app after a reboot.
 *
 * The counter phone is a fixed fixture on the dashboard. A power interruption mid-route
 * restarts it, and without this the app stays closed and every trip afterwards goes
 * uncounted until someone reaches the phone.
 *
 * Android 10 and above blocks starting an activity directly from a boot broadcast, so two
 * things are attempted: the direct launch, which some vendor builds still allow, and a
 * high-importance notification carrying a full-screen intent, which launches the app on a
 * locked device that has just booted and is a single-tap banner otherwise.
 *
 * A phone not yet bound to a bus is not a fixture and has nothing to resume, so it is left
 * closed.
 */
class BootReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return

        // The watcher is the dependable recovery path: it polls for trips and reopens the
        // UI itself, so the phone keeps counting even when both launches in relaunch are blocked.
        WatcherService.start(context)

        val pending = goAsync()
        CoroutineScope(Dispatchers.IO).launch {
            try {
                // An unreadable store is treated as bound, so a failed read never costs a
                // fixture its relaunch.
                val bound = runCatching { !Prefs(context).vehicleId.first().isNullOrBlank() }
                    .getOrDefault(true)
                // Android can deliver the boot broadcast minutes late. By then the app may
                // already be on screen, and a notice to open it would sit over it.
                if (bound && !MainActivity.uiVisible) relaunch(context)
            } finally {
                pending.finish()
            }
        }
    }

    private fun relaunch(context: Context) {
        val launch = Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

        runCatching { context.startActivity(launch) }

        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(
                CHANNEL, "Counter auto-start",
                NotificationManager.IMPORTANCE_HIGH
            ).apply { description = "Reopens the passenger counter after the phone restarts" }
        )

        val pi = PendingIntent.getActivity(
            context, 0, launch,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notif = android.app.Notification.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(0xFF3AB39C.toInt())
            .setContentTitle("RouteSync Sentinel")
            .setContentText("Phone restarted. Tap to resume counting.")
            .setContentIntent(pi)
            .setFullScreenIntent(pi, true)
            .setAutoCancel(true)
            .build()

        runCatching { nm.notify(NOTIF_ID, notif) }
    }

    companion object {
        private const val CHANNEL = "boot_relaunch"
        const val NOTIF_ID = 2001
    }
}
