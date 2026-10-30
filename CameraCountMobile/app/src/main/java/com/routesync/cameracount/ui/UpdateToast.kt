package com.routesync.cameracount.ui

import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.FileProvider
import com.routesync.cameracount.data.AppUpdate
import kotlinx.coroutines.launch
import java.io.File

/**
 * Offers a newer Sentinel as a toast that slides up from the bottom of the screen, checked
 * each time the waiting or setup screen opens.
 *
 * Updating takes a person at the phone: Android asks for confirmation on every install
 * that did not come from the Play Store, so the update happens during a planned visit.
 * It stays until dismissed rather than timing out, since nobody may be looking when it
 * appears. Never shown while counting, because the counting screen replaces this one.
 */
@Composable
fun UpdateToast(modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var offer by remember { mutableStateOf<AppUpdate.Offer?>(null) }
    var dismissed by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var progress by remember { mutableFloatStateOf(0f) }
    var note by remember { mutableStateOf<String?>(null) }

    val dir = remember { File(context.cacheDir, "updates") }

    LaunchedEffect(Unit) {
        offer = AppUpdate.check()
        // Nothing newer: an APK still kept is the one now installed, or one for a version
        // that has since been withdrawn.
        if (offer == null) dir.deleteRecursively()
    }

    fun start(found: AppUpdate.Offer) {
        // Android lets an app open the installer only once the person has allowed it, on a
        // settings page Android offers no dialog for. They tap Update again on return.
        if (!context.packageManager.canRequestPackageInstalls()) {
            note = "Allow installs from Sentinel, then tap Update again."
            runCatching {
                context.startActivity(
                    Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}"))
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                )
            }
            return
        }
        busy = true
        note = null
        progress = 0f
        scope.launch {
            try {
                // The offered version's APK is kept, so a cancelled install or a second tap opens
                // the installer again without downloading. Anything else in the folder is an
                // older version, or a download that never finished.
                dir.mkdirs()
                val apk = File(dir, "RouteSync-Sentinel-${found.version}.apk")
                dir.listFiles()?.filter { it != apk }?.forEach { it.delete() }
                if (!apk.exists()) AppUpdate.download(found.apkUrl, apk) { progress = it }
                val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileProvider", apk)
                context.startActivity(
                    Intent(Intent.ACTION_VIEW)
                        .setDataAndType(uri, "application/vnd.android.package-archive")
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_GRANT_READ_URI_PERMISSION)
                )
            } catch (e: Exception) {
                note = "Update failed. Check the connection and try again."
            } finally {
                busy = false
            }
        }
    }

    AnimatedVisibility(
        visible = offer != null && !dismissed,
        enter = slideInVertically { it } + fadeIn(),
        exit = slideOutVertically { it } + fadeOut(),
        modifier = modifier
    ) {
        val found = offer ?: return@AnimatedVisibility
        Row(
            Modifier.widthIn(max = 380.dp)
                .shadow(6.dp, RoundedCornerShape(50))
                .background(RsColor.Navy, RoundedCornerShape(50))
                .padding(start = 18.dp, end = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                note ?: if (busy) "Downloading ${(progress * 100).toInt()}%" else "Sentinel ${found.version} available",
                color = Color.White, fontSize = 15.sp, maxLines = 2,
                modifier = Modifier.weight(1f, fill = false).padding(vertical = 12.dp)
            )
            if (!busy) {
                Text(
                    "Update", color = RsColor.TealBright, fontWeight = FontWeight.Bold, fontSize = 15.sp,
                    modifier = Modifier.clickable { start(found) }.padding(horizontal = 12.dp, vertical = 12.dp)
                )
                Text(
                    "✕", color = Color.White.copy(alpha = 0.6f), fontSize = 15.sp,
                    modifier = Modifier.clickable { dismissed = true }.padding(horizontal = 10.dp, vertical = 12.dp)
                )
            } else {
                Spacer(Modifier.width(12.dp))
            }
        }
    }
}
