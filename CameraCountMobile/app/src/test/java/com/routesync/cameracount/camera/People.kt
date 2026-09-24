package com.routesync.cameracount.camera

import android.graphics.RectF

/*
 * Shared scene-building for the counting tests.
 *
 * Everything is in frame-normalized coordinates, as DetectorAnalyzer emits them. A
 * person is a 0.2 by 0.4 box, roughly how a passenger fills the doorway from the
 * dashboard. The default counting line is LineCrossCounter's own, a vertical segment at
 * x = 0.5, and with the default inward sign the boarding side is x < 0.5: a boarding is
 * a walk from right to left.
 */

const val PERSON_W = 0.2f
const val PERSON_H = 0.4f

/** A box of person size centred on the given point. */
fun personBox(cx: Float, cy: Float = 0.5f) =
    RectF(cx - PERSON_W / 2, cy - PERSON_H / 2, cx + PERSON_W / 2, cy + PERSON_H / 2)

/** One detection of a person, confident enough to start a track unless [score] says otherwise. */
fun person(cx: Float, cy: Float = 0.5f, score: Float = 0.9f) =
    YoloDetector.Det(personBox(cx, cy), score)

/** A score the detector emits but the tracker will not start a track from. */
const val LOW = YoloDetector.HIGH_CONF - 0.1f

/** Centre positions from [from] to [to] inclusive, [step] apart. */
fun walk(from: Float, to: Float, step: Float = 0.05f): List<Float> {
    val n = Math.round(kotlin.math.abs(to - from) / step)
    val dir = if (to < from) -1 else 1
    return (0..n).map { from + dir * it * step }
}

/**
 * Feeds [frames] of detections through the tracker and the line, as CameraScreen does,
 * and returns every crossing reported, in order.
 */
fun PersonTracker.run(
    counter: LineCrossCounter,
    frames: List<List<YoloDetector.Det>>
): List<Crossing> = frames.flatMap { counter.process(update(it)) }

/** One person walking through [xs], one frame per position. */
fun oneWalking(xs: List<Float>, cy: Float = 0.5f): List<List<YoloDetector.Det>> =
    xs.map { listOf(person(it, cy)) }

/** [n] frames in which the detector found nobody. */
fun nobody(n: Int): List<List<YoloDetector.Det>> = List(n) { emptyList() }

val List<Crossing>.boardings get() = count { it.direction == CrossDirection.IN }
val List<Crossing>.exits get() = count { it.direction == CrossDirection.OUT }
