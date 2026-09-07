package com.routesync.cameracount.ui

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.unit.dp

// Icons are drawn here rather than taken from material-icons-extended. That artifact
// carries every Material glyph, and this app builds with minification disabled, so the
// whole set would be packaged for the sake of one button. Stroke-only, so they stay
// legible against the black camera preview at any size.
private fun strokeIcon(name: String, vararg paths: String): ImageVector =
    ImageVector.Builder(
        name = name,
        defaultWidth = 24.dp,
        defaultHeight = 24.dp,
        viewportWidth = 24f,
        viewportHeight = 24f
    ).apply {
        paths.forEach { d ->
            addPath(
                pathData = PathParser().parsePathString(d).toNodes(),
                // Icon() tints the whole painter, so this colour is only a base.
                stroke = SolidColor(Color.White),
                strokeLineWidth = 1.7f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            )
        }
    }.build()

// A circle drawn as a path, since the builder takes path data only.
private const val PUPIL = "M12,9 a3,3 0 1 0 0,6 a3,3 0 1 0 0,-6"

object RsIcons {
    /**
     * Swap glyph: two arrows following each other around a rounded square.
     *
     * The ring is split into halves with a gap on each side, so the pair reads as two
     * arrows rather than one unbroken box.
     */
    val Cameraswitch: ImageVector by lazy {
        strokeIcon(
            "Cameraswitch",
            // Upper arrow: up the left side, over the top, down the right, head at the end.
            "M5,10.2 L5,8 A3,3 0 0 1 8,5 L16,5 A3,3 0 0 1 19,8 L19,10.2",
            "M17.9,9.1 L19,10.2 L20.1,9.1",
            // Lower arrow: the same path rotated 180 degrees, closing the loop.
            "M19,13.8 L19,16 A3,3 0 0 1 16,19 L8,19 A3,3 0 0 1 5,16 L5,13.8",
            "M6.1,14.9 L5,13.8 L3.9,14.9"
        )
    }

    /**
     * The two halves of a reveal control on a passcode field.
     *
     * The icon reports the state of the field rather than the action on offer: a slashed
     * eye while the passcode is hidden, an open one while it is readable. The dashboard
     * and the driver app draw the same pair from the same outlines.
     */
    /** Padlock, the mark the driver app puts beside a passcode field. */
    val Lock: ImageVector by lazy {
        strokeIcon(
            "Lock",
            // Body.
            "M5,11 L19,11 A1,1 0 0 1 20,12 L20,19 A1,1 0 0 1 19,20 L5,20 A1,1 0 0 1 4,19 L4,12 A1,1 0 0 1 5,11 z",
            // Shackle, standing on the body rather than crossing into it.
            "M8,11 L8,7.5 A4,4 0 0 1 16,7.5 L16,11"
        )
    }

    /** Bus, for the field that names which one this phone is bound to. */
    val Bus: ImageVector by lazy {
        strokeIcon(
            "Bus",
            "M5,17 L5,7 A3,3 0 0 1 8,4 L16,4 A3,3 0 0 1 19,7 L19,17 A2,2 0 0 1 17,19 L7,19 A2,2 0 0 1 5,17 z",
            // Window band and the two wheels.
            "M5,11 L19,11",
            "M8,19 L8,20.5",
            "M16,19 L16,20.5"
        )
    }

    val EyeOpen: ImageVector by lazy {
        strokeIcon(
            "EyeOpen",
            "M2,12 s3.5,-7 10,-7 s10,7 10,7 s-3.5,7 -10,7 s-10,-7 -10,-7 z",
            PUPIL
        )
    }

    val EyeOff: ImageVector by lazy {
        strokeIcon(
            "EyeOff",
            // The lid, opened out into two arcs so the stroke through it has somewhere to go.
            "M2,12 s3.5,-7 10,-7 c2,0 3.8,0.6 5.3,1.5",
            "M22,12 s-3.5,7 -10,7 c-2,0 -3.8,-0.6 -5.3,-1.5",
            "M3,3 L21,21",
            PUPIL
        )
    }
}
