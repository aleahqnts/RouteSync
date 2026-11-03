package com.routesync.cameracount.data

import android.content.Context
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.floatPreferencesKey
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

private val Context.dataStore by preferencesDataStore(name = "cameracount")

/**
 * Device-local settings.
 *
 * The phone is a fixed fixture on one bus, so it binds to a vehicle once. The passcode
 * guards against the phone being re-pointed at a different bus.
 */
class Prefs(private val context: Context) {

    companion object {
        private val VEHICLE_ID = stringPreferencesKey("vehicle_id")
        private val PLATE = stringPreferencesKey("plate")
        private val PASSCODE = stringPreferencesKey("passcode")
        private val DEVICE_ID = stringPreferencesKey("device_id")
        private val LINE_AX = floatPreferencesKey("line_ax")
        private val LINE_AY = floatPreferencesKey("line_ay")
        private val LINE_BX = floatPreferencesKey("line_bx")
        private val LINE_BY = floatPreferencesKey("line_by")
        private val LINE_INWARD_SIGN = intPreferencesKey("line_inward_sign")
        private val PENDING_COUNTS = stringPreferencesKey("pending_counts")
        // Superseded by PENDING_COUNTS. Read once on the next start so a count held
        // at upgrade time is carried into the list rather than dropped.
        private val PENDING_TRIP_ID = stringPreferencesKey("pending_trip_id")
        private val PENDING_COUNT = intPreferencesKey("pending_count")
        private val DEVICE_JWT = stringPreferencesKey("device_jwt")
        private val USE_BACK_CAMERA = androidx.datastore.preferences.core.booleanPreferencesKey("use_back_camera")
        private val CONFIG_VERSION = intPreferencesKey("config_version")
        // Default line runs vertically down the middle of the frame.
        const val DEF_AX = 0.5f; const val DEF_AY = 0.05f
        const val DEF_BX = 0.5f; const val DEF_BY = 0.95f
        const val DEF_INWARD_SIGN = 1

        /**
         * How many undelivered counts to hold.
         *
         * Normal operation holds none: a count is confirmed within seconds of the trip
         * ending. A backlog this long means deliveries have been failing for weeks, and
         * the oldest entries are the least likely to ever be accepted.
         */
        const val MAX_PENDING = 20

        /** How long one is kept before it is given up on. */
        const val PENDING_MAX_AGE_MS = 14L * 24 * 60 * 60 * 1000
    }

    val vehicleId: Flow<String?> = context.dataStore.data.map { it[VEHICLE_ID] }
    val plate: Flow<String?> = context.dataStore.data.map { it[PLATE] }

    /**
     * Stable per-installation identifier used to claim a trip.
     *
     * The first camera phone to claim a trip writes this to `trips.counter_device_id`. A
     * second phone bound to the same bus sees the claim and stands by rather than
     * counting the same passengers again.
     */
    suspend fun deviceId(): String {
        context.dataStore.data.first()[DEVICE_ID]?.let { return it }
        val id = "cam-" + java.util.UUID.randomUUID().toString().take(8)
        context.dataStore.edit { it[DEVICE_ID] = id }
        return id
    }

    /**
     * Device JWT minted at bind time by the device-token edge function, valid for 365 days.
     *
     * It survives an unbind because it carries only the device identifier. Vehicle scope
     * comes from the database join, so re-binding to another bus reuses the same token.
     */
    suspend fun deviceJwt(): String? = context.dataStore.data.first()[DEVICE_JWT]

    suspend fun saveDeviceJwt(jwt: String) {
        context.dataStore.edit { it[DEVICE_JWT] = jwt }
    }

    /**
     * Which camera faces the doorway, decided by how the phone is mounted and changed
     * from the calibrate screen.
     *
     * The back camera often has a 0.6x ultrawide lens, which fits the whole approach path
     * in frame at dashboard distance.
     */
    val useBackCamera: Flow<Boolean> = context.dataStore.data.map { it[USE_BACK_CAMERA] ?: false }

    suspend fun saveUseBackCamera(v: Boolean) {
        context.dataStore.edit { it[USE_BACK_CAMERA] = v }
    }

    /** Counting-line calibration: two endpoints at any angle, plus the boarding side. */
    data class LineCalibration(
        val ax: Float, val ay: Float, val bx: Float, val by: Float, val inwardSign: Int
    )

    val lineCalibration: Flow<LineCalibration> = context.dataStore.data.map {
        LineCalibration(
            it[LINE_AX] ?: DEF_AX, it[LINE_AY] ?: DEF_AY,
            it[LINE_BX] ?: DEF_BX, it[LINE_BY] ?: DEF_BY,
            it[LINE_INWARD_SIGN] ?: DEF_INWARD_SIGN
        )
    }

    suspend fun saveLine(ax: Float, ay: Float, bx: Float, by: Float, inwardSign: Int) {
        context.dataStore.edit {
            it[LINE_AX] = ax; it[LINE_AY] = ay
            it[LINE_BX] = bx; it[LINE_BY] = by
            it[LINE_INWARD_SIGN] = inwardSign
        }
    }

    // The `device_config` row is the source of truth and DataStore is the offline cache.
    // CONFIG_VERSION is the version this device last applied, or authored when the
    // calibration was made on the phone.

    suspend fun configVersion(): Int = context.dataStore.data.first()[CONFIG_VERSION] ?: 0

    suspend fun saveConfigVersion(v: Int) {
        context.dataStore.edit { it[CONFIG_VERSION] = v }
    }

    /**
     * Applies a newer remote configuration in a single edit.
     *
     * Line, lens and version land together, so a crash part-way through cannot leave a
     * cache that claims the new version while still holding the old line.
     */
    suspend fun applyRemoteConfig(
        ax: Float, ay: Float, bx: Float, by: Float,
        inwardSign: Int, useBack: Boolean, version: Int
    ) {
        context.dataStore.edit {
            it[LINE_AX] = ax; it[LINE_AY] = ay
            it[LINE_BX] = bx; it[LINE_BY] = by
            it[LINE_INWARD_SIGN] = inwardSign
            it[USE_BACK_CAMERA] = useBack
            it[CONFIG_VERSION] = version
        }
    }

    /**
     * A count made for one trip that the database has not yet confirmed storing.
     *
     * Written on every change, so a dead zone, a process kill or a reboot mid-trip does
     * not lose passengers. An entry is removed only when the database has been read back
     * and holds at least this many, which is what separates a delivered count from one
     * that was merely sent.
     *
     * [at] is when counting for the trip last moved, and is what the age bound measures.
     */
    data class PendingCount(
        val tripId: String,
        val count: Int,
        val at: Long = System.currentTimeMillis()
    )

    /**
     * Every count still awaiting confirmation, oldest first.
     *
     * There is one entry per trip rather than one in total. A trip that ended in a dead
     * zone can still be waiting when the next trip begins, and the next trip must not be
     * the thing that erases it.
     */
    suspend fun pendingCounts(): List<PendingCount> {
        val d = context.dataStore.data.first()
        val list = decodePending(d[PENDING_COUNTS]).toMutableList()

        // A count held by the previous single-slot format, carried across on first read.
        val legacyTrip = d[PENDING_TRIP_ID]
        if (legacyTrip != null && list.none { it.tripId == legacyTrip }) {
            list += PendingCount(legacyTrip, d[PENDING_COUNT] ?: 0)
        }
        return list.sortedBy { it.at }
    }

    /** The entry for one trip, or null. */
    suspend fun pendingCount(tripId: String): PendingCount? =
        pendingCounts().firstOrNull { it.tripId == tripId }

    /**
     * Records the count for a trip, replacing any earlier figure for the same one.
     *
     * The stored count never falls. Counting is monotonic, so a lower figure arriving
     * here means a stale caller, not a correction.
     */
    suspend fun savePendingCount(tripId: String, count: Int) {
        context.dataStore.edit { p ->
            val list = decodePending(p[PENDING_COUNTS]).toMutableList()
            val legacyTrip = p[PENDING_TRIP_ID]
            if (legacyTrip != null && list.none { it.tripId == legacyTrip }) {
                list += PendingCount(legacyTrip, p[PENDING_COUNT] ?: 0)
            }
            p.remove(PENDING_TRIP_ID)
            p.remove(PENDING_COUNT)

            val at = list.firstOrNull { it.tripId == tripId }?.at ?: System.currentTimeMillis()
            val was = list.firstOrNull { it.tripId == tripId }?.count ?: 0
            list.removeAll { it.tripId == tripId }
            list += PendingCount(tripId, maxOf(was, count), at)

            // Oldest first, so the bound drops the entries least likely to be accepted.
            p[PENDING_COUNTS] = encodePending(
                list.sortedBy { it.at }.takeLast(MAX_PENDING)
            )
        }
    }

    /** Drops one trip's entry, once the database has confirmed the figure. */
    suspend fun clearPendingCount(tripId: String) {
        context.dataStore.edit { p ->
            val list = decodePending(p[PENDING_COUNTS]).filterNot { it.tripId == tripId }
            p[PENDING_COUNTS] = encodePending(list)
            if (p[PENDING_TRIP_ID] == tripId) {
                p.remove(PENDING_TRIP_ID)
                p.remove(PENDING_COUNT)
            }
        }
    }

    /**
     * Removes entries older than the age bound and returns what was removed.
     *
     * Giving up on a count is an event rather than housekeeping: the caller reports it
     * instead of letting the figure disappear without anyone being told.
     */
    suspend fun prunePendingCounts(): List<PendingCount> {
        val cut = System.currentTimeMillis() - PENDING_MAX_AGE_MS
        val dropped = mutableListOf<PendingCount>()
        context.dataStore.edit { p ->
            val list = decodePending(p[PENDING_COUNTS])
            dropped += list.filter { it.at < cut }
            if (dropped.isNotEmpty()) {
                p[PENDING_COUNTS] = encodePending(list.filter { it.at >= cut })
            }
        }
        return dropped
    }

    // DataStore holds scalars, so the list travels as one JSON string. The volume is a
    // handful of small objects, rewritten once per boarding.

    private fun encodePending(list: List<PendingCount>): String {
        val arr = org.json.JSONArray()
        list.forEach {
            arr.put(
                org.json.JSONObject()
                    .put("t", it.tripId).put("c", it.count).put("at", it.at)
            )
        }
        return arr.toString()
    }

    private fun decodePending(raw: String?): List<PendingCount> {
        if (raw.isNullOrBlank()) return emptyList()
        return try {
            val arr = org.json.JSONArray(raw)
            (0 until arr.length()).mapNotNull { i ->
                val o = arr.optJSONObject(i) ?: return@mapNotNull null
                val t = o.optString("t", "")
                if (t.isEmpty()) null
                else PendingCount(t, o.optInt("c", 0), o.optLong("at", 0L))
            }
        } catch (_: Exception) {
            // Unreadable rather than absent. Counts held here are already at risk, so the
            // list is abandoned rather than allowed to throw on every read.
            emptyList()
        }
    }

    suspend fun bind(vehicleId: String, passcode: String, plate: String) {
        context.dataStore.edit {
            it[VEHICLE_ID] = vehicleId
            it[PLATE] = plate
            it[PASSCODE] = passcode
        }
    }

    suspend fun checkPasscode(input: String): Boolean =
        context.dataStore.data.first()[PASSCODE] == input

    suspend fun unbind() {
        context.dataStore.edit {
            it.remove(VEHICLE_ID)
            it.remove(PLATE)
            // The passcode only exists to gate the unbind that just happened. Keeping
            // it would leave the fleet secret on a phone bound to nothing.
            it.remove(PASSCODE)
        }
    }
}
