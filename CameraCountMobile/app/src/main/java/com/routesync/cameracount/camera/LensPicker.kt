package com.routesync.cameracount.camera

import android.hardware.camera2.CameraCharacteristics
import androidx.camera.camera2.interop.Camera2CameraInfo
import androidx.camera.core.CameraInfo
import androidx.camera.core.CameraSelector
import kotlin.math.atan2
import kotlin.math.hypot

/**
 * Finds the widest back-facing lens a device exposes.
 *
 * On some phones the ultrawide is a separate physical sensor rather than part of the
 * logical back camera's zoom range, so the minimum zoom ratio stays at 1.0 and the wider
 * lens is only reachable as its own CameraInfo. Where the vendor surfaces it, this picks
 * it by computing each back camera's diagonal field of view from sensor size and focal
 * length.
 *
 * Where nothing wider is exposed, because the vendor restricts physical camera ids to
 * system apps, the plain back selector is returned with its field of view, which the UI
 * shows so a narrow lens is visible at a glance.
 */
object LensPicker {

    data class Pick(val selector: CameraSelector, val fovDegrees: Int)

    /** Widest field of view in whole degrees among cameras with the given facing, or 0
     *  if the characteristics cannot be read. */
    fun fovDegrees(cameraInfos: List<CameraInfo>, facing: Int): Int =
        cameraInfos.filter { it.lensFacing == facing }
            .mapNotNull { fovOf(it) }
            .maxOrNull()?.toInt() ?: 0

    /** Diagonal field of view in degrees, or null if the characteristics cannot be read. */
    private fun fovOf(info: CameraInfo): Float? = try {
        val c = Camera2CameraInfo.from(info)
        val size = c.getCameraCharacteristic(CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE)
        val focals = c.getCameraCharacteristic(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)
        if (size == null || focals == null || focals.isEmpty()) null
        else {
            val diag = hypot(size.width, size.height)
            val f = focals.min() // the shortest focal length gives the widest view
            Math.toDegrees(2.0 * atan2((diag / 2.0), f.toDouble())).toFloat()
        }
    } catch (_: Exception) { null }

    /**
     * @param cameraInfos the camera infos reported by the camera provider.
     * @return the widest back-facing lens as a bindable selector, with its field of view.
     */
    fun widestBack(cameraInfos: List<CameraInfo>): Pick {
        val backs = cameraInfos.filter {
            it.lensFacing == CameraSelector.LENS_FACING_BACK
        }
        if (backs.isEmpty()) return Pick(CameraSelector.DEFAULT_BACK_CAMERA, 0)

        val scored = backs.map { it to (fovOf(it) ?: 0f) }
        val widest = scored.maxByOrNull { it.second }!!

        // A selector that pins CameraX to exactly this CameraInfo.
        val id = Camera2CameraInfo.from(widest.first).cameraId
        val selector = CameraSelector.Builder()
            .requireLensFacing(CameraSelector.LENS_FACING_BACK)
            .addCameraFilter { infos ->
                infos.filter { Camera2CameraInfo.from(it).cameraId == id }
            }
            .build()

        return Pick(selector, widest.second.toInt())
    }
}
