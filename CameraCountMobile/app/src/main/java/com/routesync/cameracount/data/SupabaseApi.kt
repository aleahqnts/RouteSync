package com.routesync.cameracount.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant
import java.util.concurrent.TimeUnit

/**
 * PostgREST client for the shared RouteSync database, using the same publishable key the
 * driver app embeds.
 *
 * Database rows are the only connection between this app and the rest of RouteSync.
 * Nothing talks to another app directly.
 */
object SupabaseApi {

    private const val BASE = "https://vrtluruqaxutecydbrsq.supabase.co/rest/v1"
    private const val FUNCTIONS = "https://vrtluruqaxutecydbrsq.supabase.co/functions/v1"
    private const val KEY = "sb_publishable_sjkjW2K7QOPRKmixJdhSgA_8rPtoFzD"
    private val JSON = "application/json".toMediaType()

    /**
     * Device JWT for the `app_camera` role, issued by the device-token edge function.
     * Loaded from DataStore at startup and refreshed on bind. When null, requests fall
     * back to the anonymous key, which has no access to the tables below.
     */
    @Volatile var deviceJwt: String? = null

    private val http = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .build()

    private fun Request.Builder.supabaseHeaders() = this
        .header("apikey", KEY)
        .header("Authorization", "Bearer ${deviceJwt ?: KEY}")

    /** Outcome of a token request. Only [Denied] means the passcode itself was wrong. */
    sealed interface TokenResult {
        data class Ok(val token: String) : TokenResult
        data object Denied : TokenResult
        data object Unreachable : TokenResult
    }

    /**
     * Exchanges the fleet passcode for a device JWT valid for 365 days.
     *
     * The passcode is compared server-side, inside the edge function, so the secret
     * itself never reaches this app.
     */
    suspend fun fetchDeviceToken(deviceId: String, fleetSecret: String): TokenResult =
        withContext(Dispatchers.IO) {
            val body = JSONObject()
                .put("device_id", deviceId)
                .put("fleet_secret", fleetSecret)
                .toString()
                .toRequestBody(JSON)
            val req = Request.Builder().url("$FUNCTIONS/device-token")
                .header("apikey", KEY)
                .post(body)
                .build()
            try {
                http.newCall(req).execute().use { res ->
                    when {
                        res.isSuccessful -> {
                            val token = JSONObject(res.body?.string() ?: "{}")
                                .optString("token", "")
                            if (token.isNotEmpty()) TokenResult.Ok(token) else TokenResult.Unreachable
                        }
                        res.code == 401 || res.code == 400 || res.code == 429 -> TokenResult.Denied
                        else -> TokenResult.Unreachable // fn not deployed / 5xx
                    }
                }
            } catch (_: Exception) {
                TokenResult.Unreachable
            }
        }

    data class ActiveTrip(val tripId: String, val totalBoarded: Int)

    /** Returns the Active trip for the bound vehicle, or null. Polled every few seconds. */
    suspend fun findActiveTrip(vehicleId: String): ActiveTrip? = withContext(Dispatchers.IO) {
        val url = "$BASE/trips?vehicle_id=eq.$vehicleId&trip_status=eq.Active" +
                "&select=trip_id,total_boarded"
        val req = Request.Builder().url(url).supabaseHeaders().get().build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("GET trips ${res.code}")
            val arr = JSONArray(res.body?.string() ?: "[]")
            if (arr.length() == 0) return@withContext null
            val row = arr.getJSONObject(0)
            ActiveTrip(row.getString("trip_id"), row.optInt("total_boarded", 0))
        }
    }

    /**
     * Claims a trip for this device, so only one camera phone ever counts it.
     *
     * The stamp applies only when the trip is unclaimed, already held by this device, or
     * its owner's heartbeat has been silent for more than 30 seconds, which lets a
     * standby phone take over from one that died or overheated. The condition lives in
     * the request filter, so two phones claiming at once produce exactly one row match.
     * The claim also seeds the heartbeat, so a freshly claimed trip never looks stale.
     */
    suspend fun claimTrip(tripId: String, deviceId: String): Boolean = withContext(Dispatchers.IO) {
        val staleCut = Instant.now().minusSeconds(30).toString()
        val url = "$BASE/trips?trip_id=eq.$tripId" +
                "&or=(counter_device_id.is.null,counter_device_id.eq.$deviceId,count_heartbeat.lt.$staleCut)"
        val body = JSONObject()
            .put("counter_device_id", deviceId)
            .put("count_heartbeat", Instant.now().toString())
            .toString()
            .toRequestBody(JSON)
        val req = Request.Builder().url(url).supabaseHeaders()
            .header("Prefer", "return=representation")
            .patch(body)
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("PATCH claim ${res.code}")
            JSONArray(res.body?.string() ?: "[]").length() > 0 // 0 rows = someone else owns it
        }
    }

    /**
     * Writes the count and the heartbeat in one request, every 5 seconds while counting.
     * Heartbeat freshness is how RouteSync knows the camera is alive.
     *
     * Guarded on ownership: if another device has taken the claim this matches no rows
     * and returns false, so the caller stands down instead of contending for the row.
     */
    suspend fun patchCount(tripId: String, totalBoarded: Int, deviceId: String): Boolean =
        withContext(Dispatchers.IO) {
            val body = JSONObject()
                .put("total_boarded", totalBoarded)
                .put("count_heartbeat", Instant.now().toString())
                .toString()
                .toRequestBody(JSON)
            val req = Request.Builder()
                .url("$BASE/trips?trip_id=eq.$tripId&counter_device_id=eq.$deviceId")
                .supabaseHeaders()
                .header("Prefer", "return=representation")
                .patch(body)
                .build()
            http.newCall(req).execute().use { res ->
                if (!res.isSuccessful) throw IllegalStateException("PATCH trips ${res.code}")
                JSONArray(res.body?.string() ?: "[]").length() > 0
            }
        }

    /**
     * Claims the vehicle row, enforcing one counter phone per bus.
     *
     * A second phone's bind is refused rather than allowed to take over, because no
     * deployment puts two phones on one bus, so a second bind is always an error. Atomic
     * in the same way as the trip claim.
     */
    suspend fun claimVehicle(vehicleId: String, deviceId: String): Boolean = withContext(Dispatchers.IO) {
        val url = "$BASE/vehicles?vehicle_id=eq.$vehicleId" +
                "&or=(counter_device_id.is.null,counter_device_id.eq.$deviceId)"
        val body = JSONObject().put("counter_device_id", deviceId).toString().toRequestBody(JSON)
        val req = Request.Builder().url(url).supabaseHeaders()
            .header("Prefer", "return=representation")
            .patch(body)
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("PATCH vehicles ${res.code}")
            JSONArray(res.body?.string() ?: "[]").length() > 0
        }
    }

    /** Releases the vehicle lock so a replacement phone can bind. */
    suspend fun releaseVehicle(vehicleId: String, deviceId: String): Unit = withContext(Dispatchers.IO) {
        val url = "$BASE/vehicles?vehicle_id=eq.$vehicleId&counter_device_id=eq.$deviceId"
        val body = JSONObject().put("counter_device_id", JSONObject.NULL).toString().toRequestBody(JSON)
        val req = Request.Builder().url(url).supabaseHeaders()
            .header("Prefer", "return=minimal")
            .patch(body)
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("PATCH vehicles ${res.code}")
        }
    }

    /**
     * The only write permitted after a trip has ended.
     *
     * A camera in a dead zone can count more passengers than the driver's manual
     * fallback recorded, and the trip can close before it reconnects. The count has to
     * land anyway, so this write is allowed against a completed trip for as long as this
     * device is still the claimed counter.
     *
     * The figure is sent plainly. Keeping it from lowering a stored total is the
     * database's job now: a trigger treats an incoming count as a claim and keeps the
     * high-water mark itself, which no client can get wrong or forget.
     *
     * @return the machine count the database holds after the write, or null when the
     *   request matched no row. Those are different outcomes and used to be identical:
     *   a lost claim, a deleted trip and a refusal by row-level security all answer 200
     *   with an empty body, so a caller that only checked the status code deleted counts
     *   that had never been stored. Nothing is cleared until this figure comes back.
     *
     * The body carries no heartbeat: the trip is over and nothing should appear alive.
     */
    suspend fun reconcileFinalCount(tripId: String, deviceId: String, totalBoarded: Int): Int? =
        withContext(Dispatchers.IO) {
            val url = "$BASE/trips?trip_id=eq.$tripId&counter_device_id=eq.$deviceId" +
                    "&select=total_boarded"
            val body = JSONObject().put("total_boarded", totalBoarded).toString().toRequestBody(JSON)
            val req = Request.Builder().url(url).supabaseHeaders()
                .header("Prefer", "return=representation")
                .patch(body)
                .build()
            http.newCall(req).execute().use { res ->
                if (!res.isSuccessful) throw IllegalStateException("PATCH reconcile ${res.code}")
                val arr = JSONArray(res.body?.string() ?: "[]")
                if (arr.length() == 0) null else arr.getJSONObject(0).optInt("total_boarded", 0)
            }
        }

    data class FleetVehicle(val vehicleId: String, val plate: String)

    /** The whole fleet, for the setup dropdown, so an installer picks instead of typing. */
    suspend fun listVehicles(): List<FleetVehicle> = withContext(Dispatchers.IO) {
        val url = "$BASE/vehicles?select=vehicle_id,plate_number&order=vehicle_id"
        val req = Request.Builder().url(url).supabaseHeaders().get().build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("GET vehicles ${res.code}")
            val arr = JSONArray(res.body?.string() ?: "[]")
            (0 until arr.length()).map {
                val row = arr.getJSONObject(it)
                FleetVehicle(row.getString("vehicle_id"), row.optString("plate_number", ""))
            }
        }
    }

    /**
     * Returns the vehicle's plate number, or null when no such vehicle exists.
     *
     * The plate is shown to the installer as confirmation that the right bus was bound.
     */
    suspend fun findVehiclePlate(vehicleId: String): String? = withContext(Dispatchers.IO) {
        val url = "$BASE/vehicles?vehicle_id=eq.$vehicleId&select=vehicle_id,plate_number"
        val req = Request.Builder().url(url).supabaseHeaders().get().build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("GET vehicles ${res.code}")
            val arr = JSONArray(res.body?.string() ?: "[]")
            if (arr.length() == 0) return@withContext null
            arr.getJSONObject(0).optString("plate_number", "")
        }
    }

    // Remote camera control. `device_config` holds the desired state, which this device
    // follows; `device_status` holds what the device reports back.

    data class DeviceConfig(
        val ax: Float, val ay: Float, val bx: Float, val by: Float,
        val inwardSign: Int, val useBackCamera: Boolean, val version: Int,
        /** When a driver last requested a wake and snapshot, or null if never. */
        val wakeRequestedAt: Instant? = null
    )

    /** The desired configuration for this device. Read on the same 4s tick as the trip poll. */
    suspend fun getDeviceConfig(deviceId: String): DeviceConfig? = withContext(Dispatchers.IO) {
        val url = "$BASE/device_config?device_id=eq.$deviceId" +
                "&select=line_ax,line_ay,line_bx,line_by,inward_sign,use_back_camera,version,wake_requested_at"
        val req = Request.Builder().url(url).supabaseHeaders().get().build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("GET device_config ${res.code}")
            val arr = JSONArray(res.body?.string() ?: "[]")
            if (arr.length() == 0) return@withContext null
            val r = arr.getJSONObject(0)
            DeviceConfig(
                ax = r.optDouble("line_ax", Prefs.DEF_AX.toDouble()).toFloat(),
                ay = r.optDouble("line_ay", Prefs.DEF_AY.toDouble()).toFloat(),
                bx = r.optDouble("line_bx", Prefs.DEF_BX.toDouble()).toFloat(),
                by = r.optDouble("line_by", Prefs.DEF_BY.toDouble()).toFloat(),
                inwardSign = r.optInt("inward_sign", Prefs.DEF_INWARD_SIGN),
                useBackCamera = r.optBoolean("use_back_camera", false),
                version = r.optInt("version", 0),
                wakeRequestedAt = r.optString("wake_requested_at", "")
                    .takeIf { it.isNotEmpty() && it != "null" }
                    ?.let { runCatching { java.time.OffsetDateTime.parse(it).toInstant() }.getOrNull() }
            )
        }
    }

    /**
     * Writes a locally made calibration up to the database, and seeds the row on first
     * boot. The upsert keeps the database authoritative even though the edit happened on
     * the phone. `updated_by` records which surface authored the version, since the
     * driver app and the dashboard write the same row.
     */
    suspend fun upsertDeviceConfig(
        deviceId: String,
        ax: Float, ay: Float, bx: Float, by: Float,
        inwardSign: Int, useBack: Boolean, version: Int
    ): Unit = withContext(Dispatchers.IO) {
        // org.json.JSONObject has no put(String, Float) overload on Android. Passing a
        // Float throws NoSuchMethodError at runtime, so these are all Double.
        val body = JSONObject()
            .put("device_id", deviceId)
            .put("line_ax", ax.toDouble()).put("line_ay", ay.toDouble())
            .put("line_bx", bx.toDouble()).put("line_by", by.toDouble())
            .put("inward_sign", inwardSign)
            .put("use_back_camera", useBack)
            .put("version", version)
            .put("updated_by", "device")
            .put("updated_at", Instant.now().toString())
            .toString().toRequestBody(JSON)
        val req = Request.Builder().url("$BASE/device_config").supabaseHeaders()
            .header("Prefer", "resolution=merge-duplicates,return=minimal")
            .post(body)
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("UPSERT device_config ${res.code}")
        }
    }

    /**
     * Reports liveness and which configuration version this device is running.
     *
     * The driver app and dashboard show a calibration as confirmed only when
     * `config_version_applied` matches `device_config.version`.
     *
     * @param justApplied stamps `applied_at`, marking a fresh apply rather than a heartbeat.
     */
    suspend fun upsertDeviceStatus(
        deviceId: String, configVersionApplied: Int, justApplied: Boolean = false,
        /** Wake lifecycle: idle, capturing, preview or applied. Null omits the field from
         *  the request body, leaving the stored value unchanged. */
        wakeState: String? = null,
        snapshotReady: Boolean = false,
        /** Counts this device is holding that the database has not confirmed. Null omits
         *  the field, for callers that are reporting something else and do not know. */
        unreconciled: Int? = null,
        /** When the oldest of those was made, so a backlog reads differently from a
         *  count that is a few seconds behind. */
        unreconciledOldest: Instant? = null
    ): Unit = withContext(Dispatchers.IO) {
        val body = JSONObject()
            .put("device_id", deviceId)
            .put("last_seen", Instant.now().toString())
            .put("config_version_applied", configVersionApplied)
            .apply {
                if (justApplied) put("applied_at", Instant.now().toString())
                if (wakeState != null) put("wake_state", wakeState)
                if (snapshotReady) put("snapshot_ready_at", Instant.now().toString())
                if (unreconciled != null) {
                    put("unreconciled_counts", unreconciled)
                    // Cleared explicitly when the backlog empties, or the dashboard would
                    // keep showing the age of a count that has since been delivered.
                    put(
                        "unreconciled_oldest_at",
                        unreconciledOldest?.toString() ?: JSONObject.NULL
                    )
                }
            }
            .toString().toRequestBody(JSON)
        val req = Request.Builder().url("$BASE/device_status").supabaseHeaders()
            .header("Prefer", "resolution=merge-duplicates,return=minimal")
            .post(body)
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("UPSERT device_status ${res.code}")
        }
    }

    // Snapshot transport. A private storage bucket holds at most one transient object
    // per device, named {device_id}.jpg, deleted as soon as it has served its purpose.

    private const val STORAGE = "https://vrtluruqaxutecydbrsq.supabase.co/storage/v1"
    private val JPEG = "image/jpeg".toMediaType()

    /** Uploads this device's snapshot, overwriting any previous one. Row-level security
     *  pins the path to the device's own JWT. */
    suspend fun uploadSnapshot(deviceId: String, jpeg: ByteArray): Unit = withContext(Dispatchers.IO) {
        val req = Request.Builder()
            .url("$STORAGE/object/camera-snapshots/$deviceId.jpg")
            .header("apikey", KEY)
            .header("Authorization", "Bearer ${deviceJwt ?: KEY}")
            .header("x-upsert", "true")
            // The object is overwritten in place on every wake, so any caching would show
            // the driver a previous doorway. The driver app also cache-busts its read.
            .header("cache-control", "no-cache")
            .post(jpeg.toRequestBody(JPEG))
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("PUT snapshot ${res.code}")
        }
    }

    /** Deletes the snapshot after an apply, a cancel or a timeout. A missing object is not an error. */
    suspend fun deleteSnapshot(deviceId: String): Unit = withContext(Dispatchers.IO) {
        val req = Request.Builder()
            .url("$STORAGE/object/camera-snapshots/$deviceId.jpg")
            .header("apikey", KEY)
            .header("Authorization", "Bearer ${deviceJwt ?: KEY}")
            .delete()
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful && res.code != 404)
                throw IllegalStateException("DELETE snapshot ${res.code}")
        }
    }

    /** Connectivity check: can this device reach the database at all? */
    suspend fun ping(): Boolean = withContext(Dispatchers.IO) {
        val req = Request.Builder()
            .url("$BASE/trips?select=trip_id&limit=1")
            .supabaseHeaders().get().build()
        http.newCall(req).execute().use { it.isSuccessful }
    }
}
