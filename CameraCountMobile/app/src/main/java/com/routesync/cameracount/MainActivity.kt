package com.routesync.cameracount

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import kotlinx.coroutines.flow.first
import com.routesync.cameracount.ui.*

/**
 * Entry point for RouteSync Sentinel, the camera-based passenger counter.
 *
 * The phone is bound to one vehicle, polls for that vehicle's active trip, and counts
 * boarding passengers from the camera while a trip runs. Styling follows the shared
 * RouteSync theme in `ui/Theme.kt`.
 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // The watcher outlives this activity and reopens it when a trip starts while the
        // app is closed or in the background. Started here so it is running from the
        // first launch after installation onwards.
        WatcherService.start(this)
        setContent { RsTheme { Root() } }
    }

    override fun onResume() { super.onResume(); uiVisible = true }
    override fun onPause() { super.onPause(); uiVisible = false }

    companion object {
        /** Watcher skips trip-launch polling while the UI is on screen. */
        @Volatile var uiVisible = false
    }
}

/** Sends the user to the overlay settings page at most once per app start. Asked again
 *  on the next start if the permission is still not granted. */
private var overlayPrompted = false

private fun promptOverlayPermission(context: android.content.Context) {
    if (overlayPrompted || android.provider.Settings.canDrawOverlays(context)) return
    overlayPrompted = true
    runCatching {
        context.startActivity(
            android.content.Intent(
                android.provider.Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                android.net.Uri.parse("package:${context.packageName}")
            ).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }
}

/** Nearest Activity for a Compose context, which may be wrapped several layers deep. */
private tailrec fun android.content.Context.activity(): android.app.Activity? = when (this) {
    is android.app.Activity -> this
    is android.content.ContextWrapper -> baseContext.activity()
    else -> null
}

/** Second back press within this window exits. Long enough to be deliberate, short
 *  enough that a back press minutes later is not treated as a confirmation. */
private const val EXIT_WINDOW_MS = 2_500L

@Composable
fun Root(vm: CounterViewModel = viewModel()) {
    var showPreview by remember { mutableStateOf(false) }
    val s = vm.state.collectAsState().value

    // Back button handling. There is no navigation stack: the app is one activity
    // switching on state, so an unhandled back press finishes the activity outright,
    // including during a trip, which stops the camera and the count with it.
    //
    // Calibrate backs out to the waiting screen. At the root the first press warns and a
    // second within the exit window closes the app. A confirmation dialog is avoided
    // deliberately: the phone is mounted and reached for one-handed, and a modal over the
    // counting screen would cover the doorway the camera is watching.
    val backContext = androidx.compose.ui.platform.LocalContext.current
    var lastBackAt by remember { mutableLongStateOf(0L) }
    BackHandler {
        if (showPreview) {
            showPreview = false
            return@BackHandler
        }
        val now = android.os.SystemClock.elapsedRealtime()
        if (now - lastBackAt < EXIT_WINDOW_MS) {
            backContext.activity()?.finish()
        } else {
            lastBackAt = now
            android.widget.Toast.makeText(
                backContext,
                if (s is CounterViewModel.UiState.Counting)
                    "Press back again to exit. Counting stops if you do."
                else "Press back again to exit",
                android.widget.Toast.LENGTH_SHORT
            ).show()
        }
    }

    // Two permissions, requested in order. API 33 and up needs notification permission
    // for the trip foreground service. The overlay permission is required for the watcher
    // to open this app when a trip starts, and Android offers no dialog for it, only a
    // settings page, so that page is opened directly. The overlay request is chained
    // after the notification dialog so the two prompts never appear at once.
    val context = androidx.compose.ui.platform.LocalContext.current
    val askNotif = androidx.activity.compose.rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.RequestPermission()
    ) { promptOverlayPermission(context) }
    LaunchedEffect(Unit) {
        if (android.os.Build.VERSION.SDK_INT >= 33 &&
            androidx.core.content.ContextCompat.checkSelfPermission(
                context, android.Manifest.permission.POST_NOTIFICATIONS
            ) != android.content.pm.PackageManager.PERMISSION_GRANTED
        ) askNotif.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        else promptOverlayPermission(context)
    }

    // While a trip is active this device is the counter. The camera and tracker run for
    // the whole trip and stop when the state leaves Counting and this screen is disposed.
    if (s is CounterViewModel.UiState.Counting) {
        CameraScreen(vm = vm)
        return
    }
    // Calibration (preview + draggable line), reachable while waiting.
    if (showPreview) {
        CameraScreen(calibrate = true, onClose = { showPreview = false })
        return
    }
    RsBackground {
        Column(
            Modifier.fillMaxSize().systemBarsPadding().padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            when (s) {
                is CounterViewModel.UiState.NeedsSetup -> SetupCard(vm, onBind = vm::bind)
                is CounterViewModel.UiState.Waiting -> WaitingCard(vm, s, onCamera = { showPreview = true })
                is CounterViewModel.UiState.Standby -> StandbyCard(vm, s)
                else -> {}
            }
        }
    }
}

@Composable
@OptIn(ExperimentalMaterial3Api::class)
private fun SetupCard(vm: CounterViewModel, onBind: (String, String, (String?) -> Unit) -> Unit) {
    var vehicle by remember { mutableStateOf("") }
    var passcode by remember { mutableStateOf("") }
    var touchedVehicle by remember { mutableStateOf(false) }
    var binding by remember { mutableStateOf(false) }
    var bindError by remember { mutableStateOf<String?>(null) }

    // The fleet list is behind authentication, so the passcode is entered first. A
    // verified passcode mints the device token, which then loads the bus dropdown.
    var fleet by remember { mutableStateOf<List<com.routesync.cameracount.data.SupabaseApi.FleetVehicle>?>(null) }
    var fleetError by remember { mutableStateOf<String?>(null) }
    var checking by remember { mutableStateOf(false) }
    var serverDown by remember { mutableStateOf(false) }

    val passOk = passcode.length >= CounterViewModel.MIN_PASSCODE

    fun submitPasscode() {
        if (!passOk || checking) return
        checking = true
        fleetError = null
        serverDown = false
        vm.prepareFleet(passcode) { list, err ->
            checking = false
            fleet = list
            fleetError = err
            serverDown = err?.contains("server") == true
        }
    }

    val vehicleOk = CounterViewModel.VEHICLE_ID_RE.matches(vehicle)

    RsWordmark("Sentinel")
    Spacer(Modifier.height(24.dp))
    RsCard {
        RsCardTitle("Set up this device")
        Spacer(Modifier.height(6.dp))
        Text(
            "Bind this phone to the bus it is mounted in.",
            color = RsColor.Muted, fontSize = 14.sp,
            textAlign = androidx.compose.ui.text.style.TextAlign.Center,
            modifier = Modifier.fillMaxWidth()
        )
        Spacer(Modifier.height(22.dp))

        var showPasscode by remember { mutableStateOf(false) }
        RsTextField(
            passcode,
            {
                passcode = it
                bindError = null
                fleet = null
                fleetError = null
                serverDown = false
                vehicle = ""
            },
            placeholder = "Fleet passcode",
            leading = { RsFieldIcon(RsIcons.Lock) },
            trailing = { PasscodeReveal(showPasscode) { showPasscode = !showPasscode } },
            visualTransformation =
                if (showPasscode) VisualTransformation.None else PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { submitPasscode() })
        )
        if (!passOk) RsHint("Enter the fleet passcode to load the bus list.")
        Spacer(Modifier.height(14.dp))

        if (fleet == null && !serverDown && !checking) {
            PrimaryButton("Confirm passcode", enabled = passOk) { submitPasscode() }
            Spacer(Modifier.height(12.dp))
        }

        when {
            checking -> Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(10.dp))
                Text("Checking passcode…", color = RsColor.Muted)
            }
            fleet != null -> {
                // A picker rules out typos, and showing the plate lets the installer
                // confirm the bus in front of them.
                var open by remember { mutableStateOf(false) }
                Box {
                    RsPickerField(
                        display = fleet!!.firstOrNull { it.vehicleId == vehicle }
                            ?.let { "${it.vehicleId} · ${it.plate}" } ?: "",
                        placeholder = "Select vehicle",
                        expanded = open,
                        onClick = { open = true },
                        trailing = { RsFieldIcon(RsIcons.Bus) }
                    )
                    DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                        fleet!!.forEach { v ->
                            DropdownMenuItem(
                                text = {
                                    Text(
                                        "${v.vehicleId} · ${v.plate}",
                                        color = RsColor.Navy, fontWeight = FontWeight.Medium
                                    )
                                },
                                onClick = { vehicle = v.vehicleId; bindError = null; open = false }
                            )
                        }
                    }
                }
            }
            serverDown -> {
                // Offline fallback: manual entry with format validation. The bind retries
                // the network regardless.
                val badFormat = touchedVehicle && vehicle.isNotEmpty() && !vehicleOk
                RsTextField(
                    vehicle,
                    {
                        // Fleet identifiers are V followed by digits, so input is uppercased,
                        // stripped of anything else and capped at four characters.
                        vehicle = it.uppercase().filter { c -> c == 'V' || c.isDigit() }.take(4)
                        touchedVehicle = true; bindError = null
                    },
                    placeholder = "Vehicle ID (e.g. V001)",
                    leading = { RsFieldIcon(RsIcons.Bus) },
                    isError = badFormat
                )
                if (badFormat) RsHint("Format: V + 3 digits, e.g. V001", error = true)
                else RsHint("Offline: type the vehicle ID from the dashboard sticker.")
            }
        }
        fleetError?.takeIf { !serverDown }?.let {
            Spacer(Modifier.height(8.dp))
            Text(it, color = RsColor.Error)
        }
        bindError?.let {
            Spacer(Modifier.height(8.dp))
            Text(it, color = RsColor.Error)
        }
        Spacer(Modifier.height(20.dp))
        PrimaryButton(
            if (binding) "Checking vehicle…" else "Bind vehicle",
            enabled = vehicleOk && passOk && !binding
        ) {
            binding = true
            onBind(vehicle, passcode) { err ->
                binding = false
                bindError = err // null = bound; Root switches to Waiting via state
            }
        }
    }
}

@Composable
private fun WaitingCard(vm: CounterViewModel, s: CounterViewModel.UiState.Waiting, onCamera: () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val prefs = remember { com.routesync.cameracount.data.Prefs(context) }

    // A bound phone whose line was never calibrated counts against the default
    // mid-screen line, which produces meaningless totals. The prompt repeats until the
    // installer has calibrated once.
    var lineIsDefault by remember { mutableStateOf(false) }
    var deviceId by remember { mutableStateOf("") }
    LaunchedEffect(Unit) {
        val cal = prefs.lineCalibration.first()
        lineIsDefault = cal.ax == com.routesync.cameracount.data.Prefs.DEF_AX &&
            cal.ay == com.routesync.cameracount.data.Prefs.DEF_AY &&
            cal.bx == com.routesync.cameracount.data.Prefs.DEF_BX &&
            cal.by == com.routesync.cameracount.data.Prefs.DEF_BY
        deviceId = prefs.deviceId()
    }

    Header(vm, s.vehicleId, onCamera)
    Spacer(Modifier.height(20.dp))
    if (lineIsDefault) {
        Row(
            Modifier.widthIn(max = 380.dp).fillMaxWidth()
                .clip(RoundedCornerShape(12.dp))
                .background(RsColor.Mint1)
                .padding(horizontal = 14.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(Modifier.weight(1f)) {
                Text("Counting line not calibrated", fontWeight = FontWeight.Bold, color = RsColor.Navy, fontSize = 14.sp)
                Text("Counts may be wrong until the line is placed on the doorway.", color = RsColor.Muted, fontSize = 12.sp)
            }
            TextButton(onClick = onCamera) { Text("Calibrate", color = RsColor.Teal, fontWeight = FontWeight.Bold) }
        }
        Spacer(Modifier.height(12.dp))
    }
    RsCard {
        Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            StatusDot(active = false)
            Spacer(Modifier.height(12.dp))
            Text("Waiting for trip", fontSize = 20.sp, fontWeight = FontWeight.Bold, color = RsColor.Navy)
            // Identifier and plate together, so a wrong bind is obvious at a glance.
            s.plate?.takeIf { it.isNotBlank() }?.let {
                Spacer(Modifier.height(4.dp))
                Text("${s.vehicleId} · $it", color = RsColor.Teal, fontWeight = FontWeight.Bold)
            }
            Spacer(Modifier.height(8.dp))
            Text(
                "Counting starts automatically when the driver starts a trip for ${s.vehicleId}.",
                color = RsColor.Muted, textAlign = TextAlign.Center
            )
            // The previous trip's result stays on screen until the next trip starts.
            s.tripSummary?.let {
                Spacer(Modifier.height(12.dp))
                Text(
                    "Last trip · $it", color = RsColor.Navy, fontWeight = FontWeight.Bold,
                    modifier = Modifier.clip(RoundedCornerShape(8.dp))
                        .background(RsColor.Mint2).padding(horizontal = 12.dp, vertical = 6.dp)
                )
            }
            // Counts this phone is holding that the database has not confirmed storing.
            // Absent in normal operation, because a count is confirmed within seconds of
            // the trip ending. Shown plainly when it is not, since the alternative is a
            // screen reporting a total that reached nobody.
            if (s.unreconciled > 0) {
                Spacer(Modifier.height(12.dp))
                Text(
                    if (s.unreconciled == 1) "1 trip count is waiting to be saved"
                    else "${s.unreconciled} trip counts are waiting to be saved",
                    color = RsColor.Error, fontWeight = FontWeight.Bold,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.clip(RoundedCornerShape(8.dp))
                        .background(RsColor.Mint1).padding(horizontal = 12.dp, vertical = 6.dp)
                )
                Spacer(Modifier.height(4.dp))
                Text(
                    "They are kept on this phone and sent again automatically.",
                    color = RsColor.Muted, fontSize = 11.sp, textAlign = TextAlign.Center
                )
            }
            s.lastError?.let {
                Spacer(Modifier.height(12.dp))
                Text(
                    "Offline, retrying…", color = RsColor.Error, fontWeight = FontWeight.Bold,
                    modifier = Modifier.clip(RoundedCornerShape(8.dp))
                        .background(RsColor.Mint1).padding(horizontal = 12.dp, vertical = 6.dp)
                )
            }
        }
    }
    // The device identifier shown here matches counter_device_id on the trips and
    // vehicles rows, which an admin needs in order to identify or clear this phone's lock.
    if (deviceId.isNotBlank()) {
        Spacer(Modifier.height(14.dp))
        Text("RouteSync Sentinel · $deviceId", color = RsColor.Muted, fontSize = 11.sp)
    }
}

/**
 * Fault screen, shown when another device already counts this bus.
 *
 * Deployment allows one counter phone per bus and the vehicle lock should make this
 * unreachable, so reaching it means two devices were bound to the same bus anyway,
 * through an offline bind race or a manually cleared lock. The trip claim has already
 * prevented the double count; this screen exists to get the cause fixed.
 *
 * If the counting device goes silent for more than 30 seconds, this device takes the
 * trip over so counting continues.
 */
@Composable
private fun StandbyCard(vm: CounterViewModel, s: CounterViewModel.UiState.Standby) {
    Header(vm, s.vehicleId, onCamera = {})
    Spacer(Modifier.height(20.dp))
    RsCard {
        Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            StatusDot(active = false)
            Spacer(Modifier.height(12.dp))
            Text("⚠ Two counter phones detected", fontSize = 20.sp, fontWeight = FontWeight.Bold, color = RsColor.Error)
            Spacer(Modifier.height(8.dp))
            Text(
                "Another device is already counting trip ${s.tripId} on ${s.vehicleId}. " +
                    "Each bus must have exactly ONE counter phone. Unbind the phone that " +
                    "doesn't belong. Counts are safe: only one device is being accepted.",
                color = RsColor.Muted, textAlign = TextAlign.Center
            )
        }
    }
}

@Composable
private fun Header(vm: CounterViewModel, vehicleId: String, onCamera: () -> Unit) {
    var showUnbind by remember { mutableStateOf(false) }
    val context = androidx.compose.ui.platform.LocalContext.current
    Row(
        Modifier.fillMaxWidth().widthIn(max = 380.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        RsWordmark("Sentinel")
        Row(verticalAlignment = Alignment.CenterVertically) {
            // Screen pinning keeps the app from being swiped away or backgrounded on a
            // mounted phone. Unpinning is a system gesture, Back and Recents together.
            TextButton(onClick = {
                runCatching { (context as? android.app.Activity)?.startLockTask() }
            }) { Text("Pin", color = RsColor.Muted, fontWeight = FontWeight.Bold) }
            TextButton(onClick = onCamera) { Text("Calibrate", color = RsColor.Navy, fontWeight = FontWeight.Bold) }
            TextButton(onClick = { showUnbind = true }) { Text(vehicleId, color = RsColor.Teal, fontWeight = FontWeight.Bold) }
        }
    }
    if (showUnbind) UnbindDialog(vm) { showUnbind = false }
}

@Composable
private fun StatusDot(active: Boolean) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(10.dp).clip(CircleShape).background(if (active) RsColor.TealBright else RsColor.Muted))
        Spacer(Modifier.width(6.dp))
        Text(if (active) "LIVE" else "IDLE", color = if (active) RsColor.TealBright else RsColor.Muted, fontSize = 12.sp, fontWeight = FontWeight.Bold)
    }
}

@Composable
private fun PrimaryButton(text: String, enabled: Boolean = true, onClick: () -> Unit) =
    RsPrimaryButton(text, enabled, onClick = onClick)

/** A field's leading or trailing mark, at the size the driver app draws them. */
@Composable
private fun RsFieldIcon(icon: androidx.compose.ui.graphics.vector.ImageVector) {
    Icon(icon, contentDescription = null, tint = RsColor.Muted, modifier = Modifier.size(18.dp))
}

/**
 * The button that reveals a passcode field, and the icon saying which state it is in.
 * Shared by both passcode fields so the two cannot drift apart.
 */
@Composable
private fun PasscodeReveal(shown: Boolean, onToggle: () -> Unit) {
    IconButton(onClick = onToggle) {
        Icon(
            if (shown) RsIcons.EyeOpen else RsIcons.EyeOff,
            contentDescription = if (shown) "Hide passcode" else "Show passcode"
        )
    }
}

@Composable
private fun UnbindDialog(vm: CounterViewModel, dismiss: () -> Unit) {
    var passcode by remember { mutableStateOf("") }
    var error by remember { mutableStateOf(false) }
    var showPasscode by remember { mutableStateOf(false) }
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text("Change vehicle", color = RsColor.Navy, fontWeight = FontWeight.Bold) },
        text = {
            Column {
                Text("Enter the bind passcode to release this phone from its bus.", color = RsColor.Muted)
                Spacer(Modifier.height(12.dp))
                RsTextField(
                    passcode, { passcode = it; error = false },
                    placeholder = "Passcode",
                    leading = { RsFieldIcon(RsIcons.Lock) },
                    trailing = { PasscodeReveal(showPasscode) { showPasscode = !showPasscode } },
                    isError = error,
                    visualTransformation =
                        if (showPasscode) VisualTransformation.None else PasswordVisualTransformation()
                )
                if (error) RsHint("Wrong passcode.", error = true)
            }
        },
        confirmButton = {
            TextButton(onClick = {
                vm.unbind(passcode) { ok -> if (ok) dismiss() else error = true }
            }) { Text("Unbind", color = RsColor.Error, fontWeight = FontWeight.Bold) }
        },
        dismissButton = { TextButton(onClick = dismiss) { Text("Cancel", color = RsColor.Muted) } }
    )
}
