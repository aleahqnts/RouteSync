package com.routesync.cameracount.data

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager

/**
 * What the phone can say about its own power.
 *
 * Charge on its own does not describe how hard a shift is on a phone. A bus with a USB
 * socket turns a drain measurement into nothing at all, and readings taken on a charging
 * phone averaged in with the rest quietly flatten the figure. Whether it was charging is
 * recorded beside the level so the two can be told apart afterwards rather than guessed at.
 */
object DeviceHealth {

    /** A charge level as a percentage, and whether the phone was plugged in when read. */
    data class Reading(val level: Int?, val charging: Boolean)

    fun read(context: Context): Reading {
        val level = runCatching {
            val manager = context.getSystemService(Context.BATTERY_SERVICE) as BatteryManager
            manager.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
                .takeIf { it in 0..100 }
        }.getOrNull()

        // The sticky broadcast rather than a registered receiver: this is asked for once
        // every few minutes, and holding a receiver open for the rest of the day to answer
        // it would cost more than the question.
        val charging = runCatching {
            val status = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            (status?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0
        }.getOrDefault(false)

        return Reading(level, charging)
    }
}
