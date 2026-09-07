package com.routesync.cameracount

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.routesync.cameracount.data.Prefs
import com.routesync.cameracount.data.SupabaseApi
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

/**
 * Bridge between the camera pipeline and the RouteSync database. Owns the trip lifecycle,
 * the passenger count, and the device's configuration follower.
 *
 * Three loops run against the bound vehicle:
 *
 * - Poll, every 4s: look for an Active trip and lock on to it or release it.
 * - Flush, every 5s: one PATCH carrying `total_boarded` and `count_heartbeat`. Heartbeat
 *   freshness is how RouteSync decides whether to show the driver's manual counter.
 * - Config follower, on the same 4s tick: reconcile `device_config` in both directions.
 *
 * The count is monotonic. It seeds from the database when a trip is acquired and only
 * ever rises, so restarts, manual handovers and reconnects reconcile without double
 * counting.
 */
class CounterViewModel(app: Application) : AndroidViewModel(app) {

    sealed interface UiState {
        data object NeedsSetup : UiState
        data class Waiting(
            val vehicleId: String,
            val lastError: String?,
            val plate: String? = null,
            val tripSummary: String? = null, // e.g. "43 boarded", shown after a trip ends
            /** Counts held for trips the database has not confirmed storing. Zero
             *  normally, and a number that does not fall means they are being refused. */
            val unreconciled: Int = 0
        ) : UiState
        /** Another camera phone owns this trip. This device watches without counting. */
        data class Standby(val vehicleId: String, val tripId: String) : UiState
        data class Counting(
            val vehicleId: String,
            val tripId: String,
            val count: Int,
            val lastFlushOk: Boolean,
            val cameraStalled: Boolean = false
        ) : UiState
    }

    private val prefs = Prefs(app)
    private val _state = MutableStateFlow<UiState>(UiState.NeedsSetup)
    val state: StateFlow<UiState> = _state

    private var pollJob: Job? = null
    private var flushJob: Job? = null
    private var tripId: String? = null
    private var count = 0
    private var vehicleId: String = ""
    private var plate: String? = null
    private var deviceId: String = ""
    private var lastSummary: String? = null

    private fun waiting(err: String? = null) =
        UiState.Waiting(vehicleId, err, plate, lastSummary, unreconciled)

    /** How many held counts the database has not confirmed, republished each poll. */
    private var unreconciled = 0

    /** When the oldest of those was made, reported so a backlog can be told from a lag. */
    private var backlogOldest: Long? = null

    /**
     * Time of the last analyzed frame, updated by the camera pipeline.
     *
     * When frames stop, the flush loop stops sending heartbeats. The silence is the
     * signal: a stale heartbeat is what makes the driver's manual counter reappear, so a
     * heartbeat sent regardless would hide a dead camera.
     */
    @Volatile private var lastFrameAt = 0L

    fun noteFrame() { lastFrameAt = android.os.SystemClock.elapsedRealtime() }
    private fun cameraStalled() =
        android.os.SystemClock.elapsedRealtime() - lastFrameAt > STALL_AFTER_MS

    init {
        viewModelScope.launch {
            deviceId = prefs.deviceId()
            // Attach the stored device JWT before the first database call.
            SupabaseApi.deviceJwt = prefs.deviceJwt()
            plate = prefs.plate.first()
            val v = prefs.vehicleId.first()
            if (v.isNullOrBlank()) _state.value = UiState.NeedsSetup
            else startPolling(v)
        }
    }

    companion object {
        /** Fleet convention: V + 3 digits (V001..V012 today, room to grow). */
        val VEHICLE_ID_RE = Regex("^V\\d{3}$")
        const val MIN_PASSCODE = 4

        /**
         * Frame silence that hands the trip to the driver's manual counter.
         *
         * Public because the camera watchdog derives its own shorter window from this
         * value. A rebind has to land before the handoff, and two independently chosen
         * numbers in two files would hold that order only by coincidence.
         */
        const val STALL_AFTER_MS = 12_000L
    }

    /**
     * First setup step: exchange the fleet passcode for a device JWT, then load the
     * vehicle list.
     *
     * The order is forced. Anonymous callers have no database access, so the token must
     * exist before the dropdown can be filled. [onResult] receives either the list or a
     * user-facing error, never both.
     */
    fun prepareFleet(passcode: String, onResult: (List<SupabaseApi.FleetVehicle>?, String?) -> Unit) {
        viewModelScope.launch {
            when (val tok = SupabaseApi.fetchDeviceToken(deviceId, passcode)) {
                is SupabaseApi.TokenResult.Ok -> {
                    prefs.saveDeviceJwt(tok.token)
                    SupabaseApi.deviceJwt = tok.token
                    val list = try { SupabaseApi.listVehicles() } catch (_: Exception) { null }
                    if (list == null) onResult(null, "Can't reach the server. Check the internet connection.")
                    else onResult(list, null)
                }
                SupabaseApi.TokenResult.Denied ->
                    onResult(null, "Wrong fleet passcode.")
                SupabaseApi.TokenResult.Unreachable ->
                    onResult(null, "Can't reach the server. Check the internet connection.")
            }
        }
    }

    /**
     * Binds this device to a vehicle, verifying that the vehicle exists before committing.
     *
     * The UI checks the format; this check catches a well-formed identifier for a bus
     * that is not in the fleet, which would otherwise leave the app waiting for a trip
     * that never arrives. [onResult] receives null on success, or a user-facing error.
     */
    fun bind(vehicle: String, passcode: String, onResult: (String?) -> Unit) {
        val v = vehicle.trim().uppercase()
        viewModelScope.launch {
            // Mint the device JWT first: every call below requires it, since anonymous
            // callers have no database access. A wrong passcode or an unreachable server
            // refuses the bind outright.
            when (val tok = SupabaseApi.fetchDeviceToken(deviceId, passcode)) {
                is SupabaseApi.TokenResult.Ok -> {
                    prefs.saveDeviceJwt(tok.token)
                    SupabaseApi.deviceJwt = tok.token
                }
                SupabaseApi.TokenResult.Denied -> {
                    onResult("Wrong fleet passcode. Binding is verified by the server now.")
                    return@launch
                }
                SupabaseApi.TokenResult.Unreachable -> {
                    onResult("Can't reach the server. Check the internet connection and try again.")
                    return@launch
                }
            }
            val p = try {
                SupabaseApi.findVehiclePlate(v)
            } catch (_: Exception) {
                onResult("Can't reach the server. Check the internet connection and try again.")
                return@launch
            }
            if (p == null) {
                onResult("Vehicle $v is not in the fleet. Double-check the ID on the dashboard sticker.")
                return@launch
            }
            // One counter phone per bus: claim the vehicle row or refuse the bind.
            val claimed = try {
                SupabaseApi.claimVehicle(v, deviceId)
            } catch (_: Exception) {
                onResult("Can't reach the server. Check the internet connection and try again.")
                return@launch
            }
            if (!claimed) {
                onResult(
                    "$v already has a counter phone bound to it. Unbind that phone first " +
                        "(or ask the admin to clear the lock)."
                )
                return@launch
            }
            prefs.bind(v, passcode, p)
            plate = p
            lastSummary = null
            startPolling(v)
            onResult(null)
        }
    }

    /** Passcode-gated: release the vehicle bind (and its DB lock) and go back to Setup. */
    fun unbind(passcode: String, onResult: (Boolean) -> Unit) {
        viewModelScope.launch {
            if (!prefs.checkPasscode(passcode)) { onResult(false); return@launch }
            stopCounting()
            pollJob?.cancel()
            // Best-effort lock release. An offline unbind still unbinds locally, leaving
            // the database lock for an admin to clear from vehicles.counter_device_id.
            runCatching { SupabaseApi.releaseVehicle(vehicleId, deviceId) }
            prefs.unbind()
            vehicleId = ""
            tripId = null
            _state.value = UiState.NeedsSetup
            onResult(true)
        }
    }

    /** Called by the camera pipeline on each inward line-cross. */
    fun increment() {
        val t = tripId ?: return
        count++
        persistPending(t)
        // Carry the sync state forward. A boarding says nothing about whether the last
        // flush reached the server, so reporting success here would show "synced" on
        // every passenger while the bus is in a dead zone.
        publishCounting(lastFlushOk = (state.value as? UiState.Counting)?.lastFlushOk ?: true)
    }

    /** Write-behind to DataStore, one small commit per change. Cheap at boarding rates. */
    private fun persistPending(t: String) {
        val c = count
        viewModelScope.launch { prefs.savePendingCount(t, c) }
    }

    private fun startPolling(vehicle: String) {
        vehicleId = vehicle
        _state.value = waiting()
        pollJob?.cancel()
        pollJob = viewModelScope.launch {
            while (true) {
                try {
                    val active = SupabaseApi.findActiveTrip(vehicleId)
                    when {
                        active != null && tripId == null -> {
                            // Claim first: only one camera phone may count a trip. Losing
                            // the claim drops this device to Standby, which retries every
                            // poll. The claim's stale-heartbeat rule allows a takeover once
                            // the owner has been silent for 30s.
                            if (!SupabaseApi.claimTrip(active.tripId, deviceId)) {
                                _state.value = UiState.Standby(vehicleId, active.tripId)
                            } else {
                                // Lock on and seed the monotonic count. If the app died
                                // mid-trip and this is still that trip, the persisted count
                                // is resumed too, so counts made in a dead zone survive a
                                // restart and flush on reconnect.
                                tripId = active.tripId
                                val saved = prefs.pendingCount(active.tripId)?.count ?: 0
                                count = maxOf(count, active.totalBoarded, saved)
                                persistPending(active.tripId)
                                lastFrameAt = android.os.SystemClock.elapsedRealtime() // camera warm-up grace
                                CountingService.start(getApplication(), vehicleId)
                                startFlushing()
                                publishCounting(lastFlushOk = true)
                            }
                        }
                        active != null && tripId == active.tripId -> {
                            // The manual counter may have run while this device was down.
                            // Absorb its total, never lower the local one.
                            count = maxOf(count, active.totalBoarded)
                            publishCounting(
                                lastFlushOk = (state.value as? UiState.Counting)?.lastFlushOk ?: true
                            )
                        }
                        active == null && tripId != null -> {
                            // Trip ended. Finalizing owns status and times, but total_boarded
                            // gets one raise-only reconcile: a count made in a dead zone can
                            // exceed what the driver's manual fallback wrote, and must still
                            // land after the trip closes. The raise-only filter makes the
                            // normal online case a no-op that matches no rows.
                            val endedTrip = tripId!!
                            val finalCount = count
                            lastSummary = "$finalCount boarded"
                            prefs.savePendingCount(endedTrip, finalCount)
                            stopCounting()
                            _state.value = waiting()
                        }
                    }
                    if (tripId == null && active == null) _state.value = waiting()
                } catch (e: kotlinx.coroutines.CancellationException) {
                    // The job is being shut down, by an unbind or by the view model
                    // going away. Rethrow so the loop actually stops rather than
                    // reporting its own cancellation as a failed poll.
                    throw e
                } catch (e: Exception) {
                    if (tripId == null) _state.value = waiting(e.message)
                    // While counting, poll errors are tolerated. The flush loop keeps trying.
                }
                // Held counts are offered on every pass, including while a later trip is
                // being counted, so one that cannot be delivered today is still being
                // tried tomorrow. Wrapped so a failure cannot break the trip poll.
                runCatching { drainPending(tripId) }

                // The config follower runs regardless of trip state, so a parked bus still
                // obeys a remote calibration. Wrapped separately so a configuration failure
                // cannot break the trip poll.
                runCatching { followDeviceConfig() }
                delay(4_000)
            }
        }
    }

    /**
     * Reconciles this device against the `device_config` row, which is authoritative.
     * DataStore holds only an offline cache, used to seed the camera before the first
     * successful read.
     *
     * One of three things happens per poll tick:
     *
     * - The row is missing or behind the local version, after a fresh install or a
     *   calibration made on the phone while offline. The local config is pushed up, so
     *   the database converges on the newest edit wherever it was made.
     * - The row is newer. It is applied in one DataStore write. The camera screen
     *   collects those flows, so the line and lens change immediately and crossing state
     *   resets. The applied version is echoed back for the driver and web views.
     * - The two are in sync. Every third tick sends a liveness heartbeat, roughly every
     *   12 seconds.
     */
    private fun backlogInstant(): java.time.Instant? =
        backlogOldest?.let { java.time.Instant.ofEpochMilli(it) }

    private var cfgTick = 0
    private suspend fun followDeviceConfig() {
        cfgTick++
        val localV = prefs.configVersion()
        val cfg = SupabaseApi.getDeviceConfig(deviceId)
        when {
            cfg == null || cfg.version < localV -> {
                val cal = prefs.lineCalibration.first()
                val back = prefs.useBackCamera.first()
                SupabaseApi.upsertDeviceConfig(
                    deviceId, cal.ax, cal.ay, cal.bx, cal.by, cal.inwardSign, back, localV
                )
                SupabaseApi.upsertDeviceStatus(
                    deviceId, localV,
                    unreconciled = unreconciled, unreconciledOldest = backlogInstant()
                )
            }
            cfg.version > localV -> {
                prefs.applyRemoteConfig(
                    cfg.ax, cfg.ay, cfg.bx, cfg.by, cfg.inwardSign, cfg.useBackCamera, cfg.version
                )
                // The snapshot exists only to place the line, so it is deleted as soon as
                // the calibration is applied. No images of bus interiors are retained.
                if (snapshotUpAt != null) {
                    snapshotUpAt = null
                    runCatching { SupabaseApi.deleteSnapshot(deviceId) }
                    SupabaseApi.upsertDeviceStatus(deviceId, cfg.version, justApplied = true, wakeState = "applied")
                } else {
                    SupabaseApi.upsertDeviceStatus(deviceId, cfg.version, justApplied = true)
                }
            }
            cfgTick % 3 == 0 -> SupabaseApi.upsertDeviceStatus(
                deviceId, localV,
                unreconciled = unreconciled, unreconciledOldest = backlogInstant()
            )
        }
        handleWake(cfg?.wakeRequestedAt, localV)
    }

    // Maintenance wake. A driver requesting a remote calibration patches
    // wake_requested_at, which makes this device capture one still: headless while
    // waiting for a trip, or a frame tap off the live analyzer while counting. The image
    // is uploaded and held in "preview" until the driver saves or cancels, or two minutes
    // pass, then deleted. No history of bus interiors accumulates.

    /** Analyzer for the running camera session, attached by the camera screen while
     *  counting. Used as the snapshot source so no second session is opened. */
    @Volatile var liveAnalyzer: com.routesync.cameracount.camera.DetectorAnalyzer? = null

    private var lastWakeHandled: java.time.Instant? = null
    private var snapshotUpAt: java.time.Instant? = null
    private var capturing = false

    private suspend fun handleWake(wakeAt: java.time.Instant?, localV: Int) {
        val now = java.time.Instant.now()

        // A wake request counts as new when it is later than the last one served and
        // less than three minutes old. The age check stops a stale row from before an
        // app restart triggering an unexpected capture.
        if (wakeAt != null && !capturing &&
            wakeAt.isAfter(now.minusSeconds(180)) &&
            (lastWakeHandled == null || wakeAt.isAfter(lastWakeHandled))
        ) {
            lastWakeHandled = wakeAt
            capturing = true
            try {
                SupabaseApi.upsertDeviceStatus(deviceId, localV, wakeState = "capturing")
                val bmp = captureFrame()
                if (bmp == null) {
                    SupabaseApi.upsertDeviceStatus(deviceId, localV, wakeState = "idle")
                    return
                }
                SupabaseApi.uploadSnapshot(deviceId, toJpeg(bmp))
                snapshotUpAt = java.time.Instant.now()
                SupabaseApi.upsertDeviceStatus(deviceId, localV, wakeState = "preview", snapshotReady = true)
            } catch (_: Exception) {
                runCatching { SupabaseApi.upsertDeviceStatus(deviceId, localV, wakeState = "idle") }
            } finally {
                capturing = false
            }
            return
        }

        // Timeout purge: a preview held for two minutes without being applied is deleted
        // and the device returns to idle. The apply path deletes it in followDeviceConfig.
        if (snapshotUpAt != null && now.isAfter(snapshotUpAt!!.plusSeconds(120))) {
            snapshotUpAt = null
            runCatching { SupabaseApi.deleteSnapshot(deviceId) }
            SupabaseApi.upsertDeviceStatus(deviceId, localV, wakeState = "idle")
        }
    }

    /** Captures one frame: a tap on the live analyzer while counting, avoiding a second
     *  camera session, or a headless one-shot capture while waiting. */
    private suspend fun captureFrame(): android.graphics.Bitmap? {
        liveAnalyzer?.let { an ->
            return kotlinx.coroutines.withTimeoutOrNull(10_000) {
                kotlinx.coroutines.suspendCancellableCoroutine { cont ->
                    an.frameTap = { bmp -> if (cont.isActive) cont.resume(bmp) {} }
                    cont.invokeOnCancellation { an.frameTap = null }
                }
            }
        }
        return com.routesync.cameracount.camera.SnapshotCapture.captureOnce(
            getApplication(), prefs.useBackCamera.first()
        )
    }

    /** Encodes to JPEG at quality 80, with the long side capped at 1280px. Enough
     *  resolution to place a line, small enough to upload over a mobile connection. */
    private fun toJpeg(src: android.graphics.Bitmap): ByteArray {
        val maxSide = 1280f
        val scale = minOf(1f, maxSide / maxOf(src.width, src.height))
        val bmp = if (scale < 1f) android.graphics.Bitmap.createScaledBitmap(
            src, (src.width * scale).toInt(), (src.height * scale).toInt(), true
        ) else src
        val out = java.io.ByteArrayOutputStream()
        bmp.compress(android.graphics.Bitmap.CompressFormat.JPEG, 80, out)
        return out.toByteArray()
    }

    private fun startFlushing() {
        flushJob?.cancel()
        flushJob = viewModelScope.launch {
            while (true) {
                delay(5_000)
                val t = tripId ?: break
                if (cameraStalled()) {
                    // With the camera dead, stop heartbeating so RouteSync reveals the
                    // manual counter. The local count is kept and persisted; if frames
                    // return, flushing resumes and the monotonic merge absorbs whatever
                    // the driver counted by hand.
                    publishCounting(lastFlushOk = false, cameraStalled = true)
                    continue
                }
                val stillOwner = try {
                    SupabaseApi.patchCount(t, count, deviceId)
                } catch (_: Exception) {
                    publishCounting(lastFlushOk = false) // offline: count persisted locally
                    continue
                }
                if (!stillOwner) {
                    // Another device took the claim, which the stale-heartbeat rule allows
                    // after 30s of silence. The local count is discarded: the new owner
                    // seeds from the database and counts on from there, so keeping it
                    // would inflate the total if this device claimed the trip again.
                    count = 0
                    val trip = t
                    stopCounting()
                    _state.value = UiState.Standby(vehicleId, trip)
                    break
                }
                publishCounting(lastFlushOk = true)
            }
        }
    }

    private fun stopCounting() {
        flushJob?.cancel()
        flushJob = null
        tripId = null
        count = 0
        CountingService.stop(getApplication())
        // The pending count is deliberately left on disk. It must survive until the
        // post-trip reconcile lands, or a count made in a dead zone would be lost.
    }

    /**
     * Offers one held count to the database and reports whether it was stored.
     *
     * Success is the figure that comes back, not the status code. A write matching no
     * row answers 200 with an empty body, and a count can match nothing for reasons that
     * are not recoverable: the claim was taken by another phone while this one was
     * offline, and row-level security then refuses every retry against the closed trip.
     * Treating that as success is what used to delete counts that had never been stored.
     */
    private suspend fun reconcileOne(held: Prefs.PendingCount): Boolean {
        if (held.count <= 0) {
            prefs.clearPendingCount(held.tripId)
            return true
        }
        val stored = try {
            SupabaseApi.reconcileFinalCount(held.tripId, deviceId, held.count)
        } catch (_: Exception) {
            return false // offline or transient, so it stays on disk
        }
        if (stored == null || stored < held.count) return false
        prefs.clearPendingCount(held.tripId)
        return true
    }

    /**
     * Offers every held count except the one still being made, then republishes how many
     * remain, both on screen and to the fleet.
     *
     * The trip being counted is skipped because the flush loop is already writing it
     * every five seconds, and its entry is not finished until the trip ends.
     */
    private suspend fun drainPending(activeTrip: String?) {
        // Whether anything was already known to be stuck. A count delivered on the pass
        // straight after its trip ended is the ordinary case and says nothing worth
        // reading; one delivered after a failure is the dead zone finally clearing.
        val hadBacklog = unreconciled > 0
        // Counts old enough to be given up on. Said out loud rather than swept away,
        // because the figure is about to stop existing anywhere at all.
        prefs.prunePendingCounts().forEach {
            lastSummary = "${it.count} boarded on ${it.tripId}, never delivered"
        }

        prefs.pendingCounts()
            .filter { it.tripId != activeTrip }
            .forEach { held ->
                if (reconcileOne(held) && hadBacklog) {
                    lastSummary = "${held.count} boarded, delivered late"
                }
            }

        val held = prefs.pendingCounts().filter { it.tripId != activeTrip }
        unreconciled = held.size
        backlogOldest = held.minByOrNull { it.at }?.at
        // The count reaches the screen on the next pass rather than here. Publishing
        // from this far down would overwrite the state the poll has just set, and the
        // one it sets on a failed poll is the offline indicator.
    }

    // cameraStalled defaults to the live answer rather than to false. Several loops
    // publish this state independently, so a false default lets whichever publishes last
    // erase a stall the others detected. Only the flush loop passes it explicitly, having
    // already evaluated it.
    private fun publishCounting(lastFlushOk: Boolean, cameraStalled: Boolean = cameraStalled()) {
        val t = tripId ?: return
        _state.value = UiState.Counting(vehicleId, t, count, lastFlushOk, cameraStalled)
    }
}
