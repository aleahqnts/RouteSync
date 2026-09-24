package com.routesync.cameracount.ui

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import com.routesync.cameracount.CounterViewModel
import com.routesync.cameracount.camera.DetectorAnalyzer
import com.routesync.cameracount.camera.LineCrossCounter
import com.routesync.cameracount.camera.PersonTracker
import com.routesync.cameracount.camera.YoloDetector
import com.routesync.cameracount.data.Prefs
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import java.util.concurrent.Executors

/**
 * Frame silence that counts as a dead camera. A normal frame gap is milliseconds even
 * under throttling, so this only trips on a real stall.
 *
 * Derived from [CounterViewModel.STALL_AFTER_MS] so a rebind always starts before the
 * trip is handed to the driver's manual counter. The 6s of headroom is fully spent:
 * detection costs up to one watchdog tick, and opening a camera costs another second
 * or two.
 */
private const val STALL_MS = CounterViewModel.STALL_AFTER_MS - 6_000L

/** Watchdog poll interval. A slower tick would eat the headroom in [STALL_MS]. */
private const val WATCHDOG_TICK_MS = 2_000L

/** Log tag for the watchdog. On screen a stall looks like nothing but a black preview. */
private const val WATCHDOG_TAG = "CameraWatchdog"

/**
 * Delay before the next rebind attempt, by consecutive failure count.
 *
 * A camera another app is holding does not come back on the second try, and retrying
 * every few seconds for a whole service day costs battery on a phone that may not be
 * charging. The delay caps rather than stopping, because whatever holds the camera can
 * release it at any time.
 */
private fun rebindBackoffMs(failures: Int): Long = when (failures) {
    1 -> 0L
    2 -> 5_000L
    3 -> 15_000L
    else -> 60_000L
}

/** Consecutive failed rebinds before the HUD reports the camera as unreachable. */
private const val REBINDS_BEFORE_GIVING_UP_QUIETLY = 3

/**
 * How long frames must keep arriving after a rebind before the camera counts as healthy.
 *
 * A camera another app is holding does not block cleanly. It yields for a moment, the
 * rebind succeeds, roughly two seconds of frames arrive, and it is taken again. Counting
 * the first frame as recovery would reset the failure count on every one of those blips,
 * so the backoff would never advance past its first step. Recovery means sustained.
 */
private const val HEALTHY_AFTER_REBIND_MS = 20_000L

/** Immutable per-frame box for the overlay, built on the analyzer thread. */
private data class OverlayBox(val l: Float, val t: Float, val r: Float, val b: Float, val counted: Boolean)

/**
 * Allocates the next `device_config` version for a calibration written on this phone.
 *
 * Re-reads the database rather than trusting the local counter, so the new version beats
 * whatever is stored at this moment, including a driver's remote save. Two writers
 * choosing the same number would desync silently, because the follower skips a write
 * whose version is merely equal. When the read fails the value falls back to
 * `max(0, local) + 1`, and the follower reconciles on reconnect.
 */
private suspend fun nextConfigVersion(prefs: Prefs): Int {
    val dev = prefs.deviceId()
    val dbV = runCatching {
        com.routesync.cameracount.data.SupabaseApi.getDeviceConfig(dev)?.version
    }.getOrNull() ?: 0
    val v = maxOf(dbV, prefs.configVersion()) + 1
    prefs.saveConfigVersion(v)
    return v
}

/**
 * Camera surface, in one of three modes.
 *
 * - [vm] not null: counting. Tracking and line crossings feed `vm.onCrossing()` against
 *   the saved per-device line. When the trip ends the caller stops passing a view model
 *   and this surface is torn down.
 * - [calibrate] true: calibration. Live preview with boxes, both line endpoints
 *   draggable onto the real doorway, boarding direction flippable, and Save persists to
 *   DataStore so the line survives restarts.
 * - Neither: plain detection preview.
 */
@Composable
fun CameraScreen(
    vm: CounterViewModel? = null,
    calibrate: Boolean = false,
    onClose: (() -> Unit)? = null
) {
    val context = LocalContext.current
    var granted by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) ==
                PackageManager.PERMISSION_GRANTED
        )
    }
    val ask = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        granted = it
    }

    LaunchedEffect(Unit) { if (!granted) ask.launch(Manifest.permission.CAMERA) }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        when {
            !granted -> CenterMsg("Camera permission needed.\nTap to grant.") {
                ask.launch(Manifest.permission.CAMERA)
            }
            else -> {
                val detector = remember { YoloDetector.tryCreate(context) }
                if (detector == null) {
                    CenterMsg(
                        "Model missing.\n\nExport YOLO11n and place it at\napp/src/main/assets/${YoloDetector.MODEL_ASSET}\n\nSee assets/README.txt for the one-line export.",
                        null
                    )
                } else {
                    DetectionSurface(detector, vm, calibrate, onClose)
                }
            }
        }
        onClose?.let {
            Button(
                onClick = it,
                modifier = Modifier.align(Alignment.TopEnd).statusBarsPadding().padding(16.dp)
            ) { Text("Close") }
        }
    }
}

@Composable
private fun DetectionSurface(
    detector: YoloDetector,
    vm: CounterViewModel?,
    calibrate: Boolean = false,
    onClose: (() -> Unit)? = null
) {
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current
    val scope = rememberCoroutineScope()
    val prefs = remember { Prefs(context) }

    // The calibrated line is two endpoints at any angle plus the boarding side. Loaded
    // once here; the drag handles edit the endpoints while calibrating.
    var ax by remember { mutableFloatStateOf(Prefs.DEF_AX) }
    var ay by remember { mutableFloatStateOf(Prefs.DEF_AY) }
    var bx by remember { mutableFloatStateOf(Prefs.DEF_BX) }
    var by by remember { mutableFloatStateOf(Prefs.DEF_BY) }
    var inwardSign by remember { mutableIntStateOf(Prefs.DEF_INWARD_SIGN) }
    var lineLoaded by remember { mutableStateOf(false) }
    // Null until the preference loads. The camera binds only afterwards, so the stored
    // lens is the one that comes up.
    var useBack by remember { mutableStateOf<Boolean?>(null) }
    LaunchedEffect(Unit) {
        val cal = prefs.lineCalibration.first()
        ax = cal.ax; ay = cal.ay; bx = cal.bx; by = cal.by; inwardSign = cal.inwardSign
        lineLoaded = true
        useBack = prefs.useBackCamera.first()
    }

    var boxes by remember { mutableStateOf<List<OverlayBox>>(emptyList()) }
    var frameW by remember { mutableIntStateOf(1) }
    var frameH by remember { mutableIntStateOf(1) }
    var inferMs by remember { mutableLongStateOf(0L) }
    var fps by remember { mutableIntStateOf(0) }
    var frames by remember { mutableIntStateOf(0) }
    var windowStart by remember { mutableLongStateOf(System.currentTimeMillis()) }
    var frameError by remember { mutableStateOf<String?>(null) }
    var lensInfo by remember { mutableStateOf<String?>(null) } // e.g. "back 108° (wide)"

    val counting = vm != null

    // Mid-trip line adjust, deliberately without a passcode: a driver correcting a
    // slipped mount while passengers board will abandon the fix if it asks for one.
    var adjusting by remember { mutableStateOf(false) }

    // Back cancels the adjustment instead of reaching the root handler's exit prompt.
    // Composed below that handler, so this one takes the press while it is enabled.
    BackHandler(enabled = adjusting) {
        scope.launch {
            val cal = prefs.lineCalibration.first()
            ax = cal.ax; ay = cal.ay; bx = cal.bx; by = cal.by; inwardSign = cal.inwardSign
            adjusting = false
        }
    }
    val editingLine = calibrate || adjusting

    // Remote configuration arrives in DataStore through the view model, so these flows
    // are collected rather than read once: the line and lens can change mid-trip. Both
    // ignore updates while the line is being edited, so a remote write cannot move the
    // handles out from under a drag.
    LaunchedEffect(Unit) {
        prefs.lineCalibration.collect { cal ->
            if (!(calibrate || adjusting)) {
                ax = cal.ax; ay = cal.ay; bx = cal.bx; by = cal.by; inwardSign = cal.inwardSign
                lineLoaded = true
            }
        }
    }
    LaunchedEffect(Unit) {
        prefs.useBackCamera.collect { back -> if (!(calibrate || adjusting)) useBack = back }
    }

    // Dim mode: 60s with nobody in frame draws a near-black overlay and drops inference
    // to one frame in four. The throttle is the real saving, since inference produces
    // more heat than the screen. Waking is immediate, driven by the analyzer callback
    // below, so the full rate resumes on the first frame containing a person.
    var lastPersonAt by remember { mutableLongStateOf(System.currentTimeMillis()) }
    var dimmed by remember { mutableStateOf(false) }

    // Camera watchdog state.
    //
    // A CameraX session can die on its own, through a HAL wedge, another app taking the
    // camera, or a thermal kill of the pipeline. It dies silently: the preview goes
    // black, the analyzer stops being called, and nothing throws. The view model reports
    // the stall but cannot repair it, because the binding is built once in the
    // AndroidView factory below.
    //
    // Incrementing [bindEpoch] re-keys that AndroidView, which tears the session down
    // and builds a new one without any interaction from the driver.
    var lastFrameMs by remember { mutableLongStateOf(android.os.SystemClock.elapsedRealtime()) }
    var bindEpoch by remember { mutableIntStateOf(0) }
    // True once repeated rebinds have failed, so the HUD can report it.
    var cameraUnreachable by remember { mutableStateOf(false) }
    fun framesFlowing() = android.os.SystemClock.elapsedRealtime() - lastFrameMs < STALL_MS

    if (counting) LaunchedEffect(Unit) {
        while (true) {
            kotlinx.coroutines.delay(5_000)
            // Never dim over a dead camera. No frames means no detections, which the
            // idle test cannot distinguish from an empty bus, so dimming would hide the
            // fault behind a second black layer.
            dimmed = System.currentTimeMillis() - lastPersonAt > 60_000 &&
                !editingLine && framesFlowing()
        }
    }

    // A dashboard phone that is not charging will stop counting when the battery runs out.
    var charging by remember { mutableStateOf(true) }
    DisposableEffect(Unit) {
        val receiver = object : android.content.BroadcastReceiver() {
            override fun onReceive(c: android.content.Context?, i: android.content.Intent?) {
                charging = (i?.getIntExtra(android.os.BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0
            }
        }
        val sticky = context.registerReceiver(
            receiver, android.content.IntentFilter(android.content.Intent.ACTION_BATTERY_CHANGED)
        )
        charging = (sticky?.getIntExtra(android.os.BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0
        onDispose { context.unregisterReceiver(receiver) }
    }

    // A dashboard phone must not sleep during a trip or a calibration.
    val view = androidx.compose.ui.platform.LocalView.current
    DisposableEffect(Unit) {
        view.keepScreenOn = true
        onDispose { view.keepScreenOn = false }
    }

    // Thermal guard: a closed bus in the sun heats the phone until the system kills the
    // app, so inference is throttled first. SEVERE infers every 2nd frame, CRITICAL and
    // above every 3rd. Listener-driven to avoid a binder call per frame. API 29 and up;
    // older devices run unthrottled.
    var thermalSkip by remember { mutableIntStateOf(1) }
    DisposableEffect(Unit) {
        if (android.os.Build.VERSION.SDK_INT >= 29) {
            val pm = context.getSystemService(android.os.PowerManager::class.java)
            val listener = android.os.PowerManager.OnThermalStatusChangedListener { status ->
                thermalSkip = when {
                    status >= android.os.PowerManager.THERMAL_STATUS_CRITICAL -> 3
                    status >= android.os.PowerManager.THERMAL_STATUS_SEVERE -> 2
                    else -> 1
                }
            }
            pm.addThermalStatusListener(listener)
            onDispose { pm.removeThermalStatusListener(listener) }
        } else onDispose { }
    }

    // The tracker and the line are driven frame by frame on the analyzer's thread and
    // changed from this one, by a line edit, a remote calibration or a camera rebind.
    // Every access holds the tracker's lock. Unguarded, a reset walking the track list
    // while a frame adds to it throws, and a frame caught half-way through a line change
    // judges old sides against the new line and posts a crossing nobody made.
    val tracker = remember { PersonTracker() }
    val lineCounter = remember { LineCrossCounter() }
    LaunchedEffect(ax, ay, bx, by, inwardSign) {
        synchronized(tracker) {
            lineCounter.ax = ax; lineCounter.ay = ay
            lineCounter.bx = bx; lineCounter.by = by
            lineCounter.inwardSign = inwardSign
            // Any line change, dragged or applied remotely, invalidates side and origin
            // history. Stale geometry counts or misses people against the previous line.
            tracker.resetCrossingState()
        }
    }

    // Watchdog loop. Declared after the tracker because a rebind has to reset it.
    //
    // The STARTED gate matters: CameraX unbinds the session whenever the activity stops,
    // so frames stop legitimately every time a call arrives or a dialog covers the
    // screen. Ungated, the loop would read that as a dead camera and rebind against a
    // lifecycle that will not accept the binding. The gate also restarts the clock on
    // the way back in, so time spent in the background is not counted as a stall.
    LaunchedEffect(Unit) {
        lifecycle.lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            lastFrameMs = android.os.SystemClock.elapsedRealtime()
            var failures = 0
            var nextAttemptAt = 0L
            var lastRebindAt = 0L
            android.util.Log.i(WATCHDOG_TAG, "watching (stall=${STALL_MS}ms)")
            while (true) {
                kotlinx.coroutines.delay(WATCHDOG_TICK_MS)
                val now = android.os.SystemClock.elapsedRealtime()
                if (framesFlowing()) {
                    // Only a sustained run counts as recovery. Clearing on the first
                    // frame back would hold a flapping camera at zero failures forever.
                    if (failures > 0 && now - lastRebindAt >= HEALTHY_AFTER_REBIND_MS) {
                        android.util.Log.i(WATCHDOG_TAG, "steady again after $failures rebind(s)")
                        failures = 0
                        cameraUnreachable = false
                    }
                    continue
                }
                if (now < nextAttemptAt) continue

                failures++
                // Several seconds of blackout leaves every track stale: the boxes were
                // last seen before the gap and their side history describes a scene that
                // has moved on. Re-associating them on the first frame back would post
                // crossings that nobody made.
                synchronized(tracker) { tracker.resetCrossingState() }
                // Grace period: the camera needs time to open before the next tick judges it.
                lastFrameMs = now
                lastRebindAt = now
                val backoff = rebindBackoffMs(failures)
                nextAttemptAt = now + STALL_MS + backoff
                cameraUnreachable = failures > REBINDS_BEFORE_GIVING_UP_QUIETLY
                android.util.Log.w(
                    WATCHDOG_TAG,
                    "no frames for ${STALL_MS}ms -> rebind #$failures " +
                        "(epoch ${bindEpoch + 1}, next try in ${(STALL_MS + backoff) / 1000}s)"
                )
                bindEpoch++
            }
        }
    }

    val executor = remember { Executors.newSingleThreadExecutor() }
    DisposableEffect(Unit) {
        onDispose {
            vm?.liveAnalyzer = null // snapshot taps fall back to headless capture
            // Order is required: stop frames, then the worker, then the interpreter.
            // Freeing native memory under a live frame crashes the process.
            runCatching { ProcessCameraProvider.getInstance(context).get().unbindAll() }
            executor.shutdown()
            detector.close() // synchronized with detect(): waits out any in-flight frame
        }
    }

    Box(Modifier.fillMaxSize()) {
        // Both keys tear the binding down and rebuild it: `back` when the lens is
        // switched, `bindEpoch` when the watchdog revives a dead session. Gated on
        // useBack being loaded so the stored lens comes up first rather than flipping.
        useBack?.let { back ->
            key(back, bindEpoch) {
                // Generation this binding belongs to. The provider future resolves
                // asynchronously and under camera contention can take longer than the
                // watchdog's retry window, so a listener from a superseded epoch may
                // still be pending after a newer one has bound. Running it would unbind
                // the working session and attach the preview to a discarded view,
                // leaving a permanently black screen with no exception raised.
                val myEpoch = bindEpoch
                AndroidView(
                    modifier = Modifier.fillMaxSize(),
                    onRelease = {
                        // AndroidView drops the view on an epoch change but frees nothing
                        // else. Without this the old session stays bound and feeds a
                        // second analyzer into the shared executor and detector until the
                        // next factory calls unbindAll, putting two inference pipelines
                        // on one thread during recovery.
                        runCatching { ProcessCameraProvider.getInstance(context).get().unbindAll() }
                    },
                    factory = { ctx ->
                        val view = PreviewView(ctx).apply { scaleType = PreviewView.ScaleType.FIT_CENTER }
                        val providerFuture = ProcessCameraProvider.getInstance(ctx)
                        providerFuture.addListener({
                            if (myEpoch != bindEpoch) {
                                android.util.Log.w(WATCHDOG_TAG, "epoch $myEpoch superseded, not binding")
                                return@addListener
                            }
                            val provider = providerFuture.get()
                            val preview = Preview.Builder().build()
                                .also { it.surfaceProvider = view.surfaceProvider }
                            val analysis = ImageAnalysis.Builder()
                                .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                                // RGBA output: toBitmap() on YUV frames is unsupported on
                                // some devices, where every frame throws and the rate drops
                                // to zero. RGBA conversion is built in.
                                .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
                                .build()

                            val wantFront = !back
                            val haveFront = provider.hasCamera(CameraSelector.DEFAULT_FRONT_CAMERA)
                            val haveBack = provider.hasCamera(CameraSelector.DEFAULT_BACK_CAMERA)
                            val front = if (wantFront) haveFront else !haveBack // fall back to whatever exists
                            // For the back camera, pick the widest physical lens the vendor
                            // exposes, which is a separate CameraInfo on devices with an
                            // ultrawide. The front camera uses the default.
                            val backPick = if (!front)
                                com.routesync.cameracount.camera.LensPicker.widestBack(provider.availableCameraInfos)
                            else null
                            val selector = when {
                                front -> CameraSelector.DEFAULT_FRONT_CAMERA
                                else -> backPick!!.selector
                            }
                            lensInfo = when {
                                front -> "front ${com.routesync.cameracount.camera.LensPicker.fovDegrees(provider.availableCameraInfos, CameraSelector.LENS_FACING_FRONT)}°"
                                else -> "back ${backPick!!.fovDegrees}°"
                            }

                            val analyzer =
                                DetectorAnalyzer(
                                    detector, mirrored = front,
                                    onError = { frameError = it },
                                    // Resting throttles harder than thermal does. Whichever
                                    // asks for fewer inferences wins.
                                    throttle = { maxOf(thermalSkip, if (dimmed) 4 else 1) },
                                    // Camera liveness, not counting liveness. Fires for every
                                    // delivered frame, so a detector fault leaves this ticking
                                    // and the watchdog stays out of it. Rebinding cannot repair
                                    // an interpreter, and `detector` outlives every epoch.
                                    onFrame = { lastFrameMs = android.os.SystemClock.elapsedRealtime() }
                                ) { dets, w, h, ms ->
                                    vm?.noteFrame() // stall guard: silence -> heartbeat stops -> manual fallback
                                    if (dets.isNotEmpty()) {
                                        lastPersonAt = System.currentTimeMillis()
                                        // Wake immediately rather than waiting for the 5s loop,
                                        // so the full inference rate resumes on the next frame.
                                        if (dimmed) dimmed = false
                                    }
                                    boxes = if (counting) {
                                        // Counting path: track identities, then line crossings,
                                        // then the count. The line is not consulted at all
                                        // until the saved one has loaded or while it is being
                                        // dragged. Judging a track against it marks the track
                                        // as counted, so a line dragged across someone waiting
                                        // to board would otherwise use up their boarding
                                        // without recording it. The reset that follows every
                                        // line change re-establishes their side afterwards.
                                        val tracks = synchronized(tracker) {
                                            tracker.update(dets).also { live ->
                                                if (lineLoaded && !adjusting) {
                                                    lineCounter.process(live)
                                                        .forEach { vm!!.onCrossing(it.direction) }
                                                }
                                            }
                                        }
                                        tracks.map {
                                            OverlayBox(it.box.left, it.box.top, it.box.right, it.box.bottom, it.counted)
                                        }
                                    } else {
                                        dets.filter { it.score >= YoloDetector.HIGH_CONF }
                                            .map { OverlayBox(it.box.left, it.box.top, it.box.right, it.box.bottom, false) }
                                    }
                                    frameW = w; frameH = h; inferMs = ms; frameError = null
                                    frames++
                                    val now = System.currentTimeMillis()
                                    if (now - windowStart >= 1000) {
                                        fps = frames; frames = 0; windowStart = now
                                    }
                                }
                            analysis.setAnalyzer(executor, analyzer)
                            // While counting, remote snapshots tap this analyzer's frames
                            // rather than opening a second camera session.
                            vm?.liveAnalyzer = analyzer
                            provider.unbindAll()
                            val cam = runCatching {
                                provider.bindToLifecycle(lifecycle, selector, preview, analysis)
                            }.onFailure {
                                // Camera held elsewhere or disabled by policy. The watchdog
                                // sees no frames and retries on its own schedule.
                                android.util.Log.e(WATCHDOG_TAG, "bind failed (epoch $myEpoch): ${it.message}")
                            }.getOrNull() ?: return@addListener
                            android.util.Log.i(WATCHDOG_TAG, "bound epoch $myEpoch, $lensInfo")
                            if (!front) {
                                // A minimum zoom below 1.0 means an ultrawide lens, which at
                                // dashboard distance fits the whole doorway and the approach
                                // to it in frame.
                                val minZoom = cam.cameraInfo.zoomState.value?.minZoomRatio ?: 1f
                                if (minZoom < 1f) cam.cameraControl.setZoomRatio(minZoom)
                            }
                        }, ContextCompat.getMainExecutor(ctx))
                        view
                    }
                )
            }
        }

        // Boxes and counting line, in frame-normalized coordinates mapped into the
        // FIT_CENTER rectangle. While editing, a drag grabs whichever endpoint is nearer
        // the touch.
        var activeHandle by remember { mutableIntStateOf(-1) } // 0=A, 1=B
        val canvasModifier = if (editingLine) {
            Modifier.fillMaxSize().pointerInput(frameW, frameH) {
                fun norm(px: Float, py: Float): Pair<Float, Float> {
                    val scale = minOf(size.width.toFloat() / frameW, size.height.toFloat() / frameH)
                    val cw = frameW * scale; val ch = frameH * scale
                    val ox = (size.width - cw) / 2f; val oy = (size.height - ch) / 2f
                    return ((px - ox) / cw).coerceIn(0f, 1f) to ((py - oy) / ch).coerceIn(0f, 1f)
                }
                detectDragGestures(
                    onDragStart = { pos ->
                        val (nx, ny) = norm(pos.x, pos.y)
                        val da = (nx - ax) * (nx - ax) + (ny - ay) * (ny - ay)
                        val db = (nx - bx) * (nx - bx) + (ny - by) * (ny - by)
                        activeHandle = if (da <= db) 0 else 1
                    },
                    onDragEnd = { activeHandle = -1 },
                    onDrag = { change, _ ->
                        val (nx, ny) = norm(change.position.x, change.position.y)
                        if (activeHandle == 0) { ax = nx; ay = ny } else { bx = nx; by = ny }
                    }
                )
            }
        } else Modifier.fillMaxSize()
        Canvas(canvasModifier) {
            val scale = minOf(size.width / frameW, size.height / frameH)
            val cw = frameW * scale
            val ch = frameH * scale
            val ox = (size.width - cw) / 2f
            val oy = (size.height - ch) / 2f
            fun pt(nx: Float, ny: Float) = Offset(ox + nx * cw, oy + ny * ch)
            boxes.forEach { b ->
                drawRect(
                    color = if (b.counted) Color(0xFF9AA3B2) else RsColor.TealBright,
                    topLeft = Offset(ox + b.l * cw, oy + b.t * ch),
                    size = Size((b.r - b.l) * cw, (b.b - b.t) * ch),
                    style = Stroke(width = 4f)
                )
            }
            if (counting || calibrate) {
                val pa = pt(ax, ay)
                val pb = pt(bx, by)
                drawLine(
                    color = Color(0xFFFFC94D), start = pa, end = pb,
                    strokeWidth = if (editingLine) 8f else 5f,
                    pathEffect = PathEffect.dashPathEffect(floatArrayOf(28f, 18f))
                )
                // Inward arrow: perpendicular to the line at its midpoint, pointing to the
                // boarding side given by inwardSign. For a line direction (dx, dy) the
                // normal is (-dy, dx).
                val mx = (pa.x + pb.x) / 2f; val my = (pa.y + pb.y) / 2f
                var ndx = -(pb.y - pa.y); var ndy = (pb.x - pa.x)
                val len = kotlin.math.hypot(ndx, ndy).coerceAtLeast(1f)
                ndx = ndx / len * inwardSign; ndy = ndy / len * inwardSign
                val tip = Offset(mx + ndx * 60f, my + ndy * 60f)
                drawLine(Color(0xFFFFC94D), Offset(mx, my), tip, strokeWidth = 6f)
                drawCircle(Color(0xFFFFC94D), 10f, tip)
                if (editingLine) {
                    drawCircle(Color.White, 26f, pa); drawCircle(Color(0xFFFFC94D), 18f, pa)
                    drawCircle(Color.White, 26f, pb); drawCircle(Color(0xFFFFC94D), 18f, pb)
                }
            }
        }

        Column(
            Modifier.align(Alignment.TopStart).statusBarsPadding().padding(16.dp)
                .background(Color(0xAA000000)).padding(horizontal = 10.dp, vertical = 6.dp)
        ) {
            Text("persons: ${boxes.size}", color = RsColor.TealBright, fontWeight = FontWeight.Bold)
            Text(
                "$fps fps · ${inferMs}ms · ${if (detector.usingGpu) "GPU" else "CPU"}",
                color = Color.White, fontSize = 12.sp
            )
            lensInfo?.let {
                Text(it, color = RsColor.Muted, fontSize = 11.sp)
            }
            frameError?.let {
                Text(it, color = Color(0xFFFF6B6B), fontSize = 11.sp)
            }
            // Repeated rebinds with no frames means something outside this app holds
            // the camera. Report it rather than leaving a black screen that suggests a
            // fix is imminent.
            if (cameraUnreachable) {
                Text(
                    "camera not responding, still retrying",
                    color = Color(0xFFFFC94D), fontSize = 11.sp
                )
            }
        }

        // Calibration controls, shown for both the standalone calibrate screen and the
        // mid-trip adjustment.
        if (editingLine) {
            Column(
                Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(bottom = 28.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Text(
                    "Drag the two dots to place the line across the pathway",
                    color = Color.White, fontSize = 13.sp,
                    modifier = Modifier.background(Color(0xAA000000)).padding(horizontal = 10.dp, vertical = 4.dp)
                )
                Spacer(Modifier.height(12.dp))
                Row {
                    OutlinedButton(onClick = { inwardSign = -inwardSign }) {
                        Text("Flip boarding side", color = Color.White)
                    }
                    Spacer(Modifier.width(12.dp))
                    Button(onClick = {
                        scope.launch {
                            prefs.saveLine(ax, ay, bx, by, inwardSign)
                            // Side and origin history describes the previous line, so
                            // clear it before anyone is counted against stale geometry.
                            synchronized(tracker) { tracker.resetCrossingState() }
                            // A calibration made on the phone authors a new version and
                            // pushes it up, keeping the database row authoritative.
                            // Offline the push fails and the follower reconciles on a
                            // later poll.
                            val v = nextConfigVersion(prefs)
                            runCatching {
                                com.routesync.cameracount.data.SupabaseApi.upsertDeviceConfig(
                                    prefs.deviceId(), ax, ay, bx, by, inwardSign,
                                    prefs.useBackCamera.first(), v
                                )
                            }
                            if (adjusting) adjusting = false else onClose?.invoke()
                        }
                    }) { Text("Save line", fontWeight = FontWeight.Bold) }
                }
                if (adjusting) {
                    Spacer(Modifier.height(8.dp))
                    TextButton(onClick = {
                        // Reload the saved line and drop the drag.
                        scope.launch {
                            val cal = prefs.lineCalibration.first()
                            ax = cal.ax; ay = cal.ay; bx = cal.bx; by = cal.by; inwardSign = cal.inwardSign
                            adjusting = false
                        }
                    }) { Text("Cancel", color = Color.White) }
                }
            }
        }

        // Lens switch. Placed at the top with the camera controls rather than with the
        // line editing controls below. TopCenter is the only free corner: the HUD holds
        // TopStart and the calibrate screen's Close button holds TopEnd.
        if (editingLine) {
            IconButton(
                onClick = {
                    val next = !(useBack ?: false)
                    scope.launch {
                        prefs.saveUseBackCamera(next)
                        // Lens choice is part of `device_config`, so it is pushed with the
                        // saved line rather than a drag still in progress. Offline the
                        // push fails and the follower reconciles later.
                        val v = nextConfigVersion(prefs)
                        runCatching {
                            val cal = prefs.lineCalibration.first()
                            com.routesync.cameracount.data.SupabaseApi.upsertDeviceConfig(
                                prefs.deviceId(), cal.ax, cal.ay, cal.bx, cal.by,
                                cal.inwardSign, next, v
                            )
                        }
                    }
                    useBack = next
                },
                modifier = Modifier.align(Alignment.TopCenter).statusBarsPadding().padding(top = 12.dp)
                    .clip(CircleShape).background(Color(0xAA000000))
            ) {
                Icon(
                    RsIcons.Cameraswitch,
                    contentDescription = if (useBack == true) "Switch to front camera" else "Switch to back camera",
                    tint = Color.White
                )
            }
        }

        // Entry point for a mid-trip adjustment. Counting continues, but crossings are
        // ignored while the line is being dragged.
        if (counting && !editingLine) {
            OutlinedButton(
                onClick = { adjusting = true; lastPersonAt = System.currentTimeMillis() },
                modifier = Modifier.align(Alignment.TopEnd).statusBarsPadding().padding(16.dp)
            ) { Text("Adjust line", color = Color.White, fontSize = 12.sp) }
        }

        // Not-charging warning. The counter stops when the battery does.
        if (counting && !charging) {
            Text(
                "⚡ Not charging",
                color = Color(0xFFFFC94D), fontSize = 13.sp, fontWeight = FontWeight.Bold,
                modifier = Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(bottom = 176.dp)
                    .clip(RoundedCornerShape(8.dp)).background(Color(0xCC000000))
                    .padding(horizontal = 12.dp, vertical = 6.dp)
            )
        }

        // Counting HUD: live count, trip, and sync state from the view model.
        if (vm != null && !adjusting) {
            val s = vm.state.collectAsState().value
            if (s is CounterViewModel.UiState.Counting) {
                // Each increment flashes the number white and scales it briefly, so a
                // driver glancing at the screen can see that a count registered.
                val flash = remember { androidx.compose.animation.core.Animatable(0f) }
                LaunchedEffect(s.count) {
                    if (s.count > 0) {
                        flash.snapTo(1f)
                        flash.animateTo(0f, androidx.compose.animation.core.tween(650))
                    }
                }
                Column(
                    Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(bottom = 32.dp)
                        .clip(RoundedCornerShape(18.dp))
                        .background(Color(0xCC10231F))
                        .padding(horizontal = 28.dp, vertical = 14.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Text(
                        "${s.count}",
                        color = androidx.compose.ui.graphics.lerp(RsColor.TealBright, Color.White, flash.value),
                        fontSize = 56.sp, fontWeight = FontWeight.ExtraBold,
                        modifier = Modifier.graphicsLayer {
                            val sc = 1f + 0.14f * flash.value
                            scaleX = sc; scaleY = sc
                        }
                    )
                    Text("passengers boarded", color = Color.White, fontSize = 13.sp)
                    Spacer(Modifier.height(4.dp))
                    Text(
                        "${s.tripId} · " + when {
                            s.cameraStalled -> "camera stalled, driver counting"
                            s.lastFlushOk -> "synced"
                            else -> "sync retrying"
                        },
                        color = if (s.lastFlushOk && !s.cameraStalled) RsColor.TealBright else Color(0xFFFF6B6B),
                        fontSize = 12.sp
                    )
                }
            }
        }

        // Dim overlay, drawn last so it covers everything. An idle stop or a long empty
        // stretch draws a near-black screen to save heat, battery and OLED wear.
        // Detection keeps running; a person entering frame or a tap wakes it.
        if (counting && dimmed) {
            val dimCount = (vm?.state?.collectAsState()?.value as? CounterViewModel.UiState.Counting)?.count
            Column(
                Modifier.fillMaxSize().background(Color(0xF2000000))
                    .pointerInput(Unit) {
                        detectTapGestures { lastPersonAt = System.currentTimeMillis(); dimmed = false }
                    },
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                dimCount?.let {
                    Text("$it", color = Color(0x559AE0D4), fontSize = 44.sp, fontWeight = FontWeight.Bold)
                }
                Spacer(Modifier.height(8.dp))
                Text("counting · screen resting", color = Color(0x44FFFFFF), fontSize = 12.sp)
            }
        }
    }
}

@Composable
private fun CenterMsg(text: String, onTap: (() -> Unit)?) {
    Column(
        Modifier.fillMaxSize().padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Text(text, color = Color.White, fontSize = 15.sp)
        onTap?.let {
            Spacer(Modifier.height(16.dp))
            Button(onClick = it) { Text("Grant camera access") }
        }
    }
}
