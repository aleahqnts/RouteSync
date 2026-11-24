package com.routesync.cameracount.camera

import android.graphics.RectF

/**
 * Multi-object tracker: two-stage IoU association with constant-velocity prediction,
 * following the ByteTrack approach.
 *
 * The first stage matches existing tracks against high-confidence detections. The second
 * rescues any still-unmatched track using low-confidence detections, which is what keeps
 * a person half-hidden by the door frame on the same identity instead of respawning them
 * as a new track and counting them twice.
 *
 * Only high-confidence detections create a track, and a track must survive [MIN_HITS]
 * frames before it can be counted, so single-frame artefacts such as hands or glare
 * never become countable.
 *
 * All coordinates are frame-normalized, 0 to 1, the same space DetectorAnalyzer emits.
 */
class PersonTracker {

    class Track internal constructor(val id: Int, var box: RectF, var score: Float) {
        var vx = 0f
        var vy = 0f
        var hits = 1
        var misses = 0
        /** Set once this track's boarding is counted, so a person counts exactly once. */
        var counted = false
        /**
         * When this track's boarding was counted, or 0 when it is not counted.
         *
         * Stepping back over the line within [LineCrossCounter.undoMs] of this takes the
         * boarding back, which is what keeps someone who only leans in to ask from
         * staying counted.
         */
        var countedAt = 0L
        /**
         * Set once this track has crossed outward, so a person records at most one exit.
         *
         * Independent of [counted]. Someone who boards and then steps back off sets
         * both, which is the pair of events that shows the boarding was withdrawn.
         */
        var exited = false
        /** Side of the counting line on the previous frame. 0 means undecided. */
        var prevSide = 0
        /**
         * Side this track was first seen on, recorded on the first frame it cleared the
         * dead band.
         *
         * Only tracks born on the outward side can ever be counted. That excludes someone
         * already aboard who steps back over the line and returns, the driver standing
         * near it, and any track that appears mid-frame.
         */
        var originSide = 0
        val cx get() = box.centerX()
        val cy get() = box.centerY()
        val confirmed get() = hits >= MIN_HITS
    }

    companion object {
        private const val IOU_GATE = 0.25f
        private const val MAX_MISSES = 12 // roughly 1.2s at 10fps before a track is dropped
        private const val MIN_HITS = 3
    }

    private var nextId = 1
    private val tracks = mutableListOf<Track>()

    /** Tracks dropped by the most recent [update], so a boarding that may still be taken
     *  back is not lost along with its track. */
    val dropped = ArrayList<Track>()

    /**
     * Clears side and origin history for every track, so both re-establish on the next
     * frame.
     *
     * Called when the counting line moves or after a gap in frames, since the stored
     * sides describe geometry or a scene that no longer applies. [Track.counted] and
     * [Track.exited] are deliberately preserved: a person already recorded must not be
     * recorded again.
     */
    fun resetCrossingState() {
        for (t in tracks) {
            t.prevSide = 0
            t.originSide = 0
        }
    }

    /** Consumes one frame of detections and returns the live confirmed tracks, used for
     *  both counting and the overlay. */
    fun update(dets: List<YoloDetector.Det>): List<Track> {
        // Predict every track one frame forward.
        for (t in tracks) t.box.offset(t.vx, t.vy)

        val unmatchedTracks = tracks.toMutableList()
        val high = dets.filter { it.score >= YoloDetector.HIGH_CONF }.toMutableList()
        val low = dets.filter { it.score < YoloDetector.HIGH_CONF }.toMutableList()

        associate(unmatchedTracks, high)
        associate(unmatchedTracks, low)

        // Anything still unmatched ages, and is dropped once too old.
        dropped.clear()
        for (t in unmatchedTracks) if (++t.misses > MAX_MISSES) dropped.add(t)
        tracks.removeAll(dropped)

        // Leftover high-confidence detections become new tracks. Low-confidence ones never do.
        for (d in high) tracks.add(Track(nextId++, RectF(d.box), d.score))

        return tracks.filter { it.confirmed && it.misses == 0 }
    }

    /** Greedy best-IoU matching, which is sufficient at doorway distances. */
    private fun associate(
        unmatchedTracks: MutableList<Track>,
        unmatchedDets: MutableList<YoloDetector.Det>
    ) {
        while (unmatchedTracks.isNotEmpty() && unmatchedDets.isNotEmpty()) {
            var bestIou = IOU_GATE
            var bestT: Track? = null
            var bestD: YoloDetector.Det? = null
            for (t in unmatchedTracks) for (d in unmatchedDets) {
                val i = iou(t.box, d.box)
                if (i > bestIou) { bestIou = i; bestT = t; bestD = d }
            }
            val t = bestT ?: return
            val d = bestD ?: return
            // Smoothed velocity from the centre delta. The box was already predicted forward
            // by the current velocity, so that is taken back off to measure the step from
            // where the track actually was. Measured from the prediction instead, the
            // estimate settles at half the true speed, and a walker hidden for a few frames
            // falls out of the IoU gate and is lost mid-crossing.
            val ncx = d.box.centerX()
            val ncy = d.box.centerY()
            t.vx = 0.6f * (ncx - (t.cx - t.vx)) + 0.4f * t.vx
            t.vy = 0.6f * (ncy - (t.cy - t.vy)) + 0.4f * t.vy
            t.box = RectF(d.box)
            t.score = d.score
            t.hits++
            t.misses = 0
            unmatchedTracks.remove(t)
            unmatchedDets.remove(d)
        }
    }

    private fun iou(a: RectF, b: RectF): Float {
        val ix = maxOf(0f, minOf(a.right, b.right) - maxOf(a.left, b.left))
        val iy = maxOf(0f, minOf(a.bottom, b.bottom) - maxOf(a.top, b.top))
        val inter = ix * iy
        val union = a.width() * a.height() + b.width() * b.height() - inter
        return if (union <= 0f) 0f else inter / union
    }
}

/** Which way a track went through the counting line. The wire value is what the
 *  database stores, and only [IN] is ever counted. */
enum class CrossDirection(val wire: String) { IN("in"), OUT("out") }

/**
 * One track passing through the line in one direction.
 *
 * [undo] marks an outward crossing that takes back the same person's boarding, made
 * moments earlier, so the count falls by one.
 */
data class Crossing(val trackId: Int, val direction: CrossDirection, val undo: Boolean = false)

/**
 * Detects crossings of a counting line.
 *
 * The line is a segment between endpoints A and B in frame-normalized coordinates, so it
 * can sit at any angle. Real doorways are rarely perfectly vertical.
 *
 * An inward crossing counts a boarding at once, but only when all three hold:
 *
 * - The track was born on the outward side. Anyone first seen inward, whether already
 *   aboard, the driver, or someone who walked into view from inside the bus and stayed,
 *   can never be counted, even after wandering across the line and back.
 * - The track centre clears the dead band of [BAND] perpendicular distance before a side
 *   is treated as entered. A hand hovering over the line jitters inside the band and
 *   never registers.
 * - The track has not already been counted.
 *
 * Stepping back over the line within [undoMs] of being counted takes the boarding back,
 * so someone who climbs in to ask and then leaves ends up uncounted. This holds across a
 * moment of being lost by the tracker (see [holdDropped]), which is common for someone
 * standing close to the camera. Taking a boarding back makes the person countable again.
 *
 * Any other outward crossing is never counted. It exists to be recorded, because a camera
 * that saw thirty people leave and a camera that saw nothing at all both report zero
 * boardings, and only the exits tell them apart. Each track reports at most one of these.
 *
 * @param inwardSign selects which side of A to B counts as boarding. Sides are stored
 *   relative to it: 1 is inward, -1 is outward.
 */
class LineCrossCounter(
    var ax: Float = 0.5f, var ay: Float = 0.05f,
    var bx: Float = 0.5f, var by: Float = 0.95f,
    var inwardSign: Int = 1,
    /** How long after being counted a step back over the line still takes the boarding
     *  back. Later than this, leaving is an ordinary exit. */
    var undoMs: Long = 15_000L
) {
    companion object {
        /** Dead-band half-width in frame-normalized units, about 2% of the frame. */
        private const val BAND = 0.02f
        /** How far a new track may appear from where a counted one was lost and still be
         *  taken as the same person, in frame-normalized units. */
        private const val REACQUIRE = 0.2f
    }

    /** A boarding that can still be taken back whose track was lost: where it was last
     *  seen and when it was counted. */
    private class Held(val x: Float, val y: Float, val countedAt: Long)

    private val held = ArrayList<Held>()

    /** Perpendicular distance from the line, signed so that positive is the inward side. */
    private fun inwardDist(px: Float, py: Float): Float {
        val dx = bx - ax
        val dy = by - ay
        val len = kotlin.math.hypot(dx, dy).coerceAtLeast(1e-4f)
        val cross = dx * (py - ay) - dy * (px - ax)
        return cross / len * inwardSign
    }

    private fun undoable(t: PersonTracker.Track, nowMs: Long) =
        t.counted && nowMs - t.countedAt <= undoMs

    /** Returns every crossing detected in this frame, inward and outward. */
    fun process(tracks: List<PersonTracker.Track>, nowMs: Long): List<Crossing> {
        val crossings = ArrayList<Crossing>(0)
        held.removeAll { nowMs - it.countedAt > undoMs }
        for (t in tracks) {
            val d = inwardDist(t.cx, t.cy)
            val zone = when {
                d > BAND -> 1   // clearly inward
                d < -BAND -> -1 // clearly outward
                else -> 0       // inside the dead band: keep previous state
            }
            if (zone == 0 || zone == t.prevSide) continue
            if (t.prevSide == 0) {
                t.prevSide = zone
                t.originSide = zone
                // A new track where a counted one was lost is that person found again. Found
                // back outside, they have already stepped out, so the boarding is taken back.
                val h = held.minByOrNull { kotlin.math.hypot(it.x - t.cx, it.y - t.cy) }
                if (h != null && kotlin.math.hypot(h.x - t.cx, h.y - t.cy) <= REACQUIRE) {
                    held.remove(h)
                    t.originSide = -1
                    if (zone == 1) {
                        t.counted = true
                        t.countedAt = h.countedAt
                    } else {
                        crossings += Crossing(t.id, CrossDirection.OUT, undo = true)
                    }
                }
                continue
            }
            if (zone == 1 && t.originSide == -1 && !t.counted) {
                t.counted = true
                t.countedAt = nowMs
                crossings += Crossing(t.id, CrossDirection.IN)
            } else if (zone == -1) {
                // Reaching the outward side at all means the track was inward a moment
                // ago, since a side is only left by crossing. No origin test is needed on
                // this branch, and none would be right: the two tracks that leave are
                // someone who was already aboard and someone who just stepped in, and both
                // of those went out.
                if (undoable(t, nowMs)) {
                    t.counted = false
                    t.countedAt = 0L
                    crossings += Crossing(t.id, CrossDirection.OUT, undo = true)
                } else if (!t.exited) {
                    t.exited = true
                    crossings += Crossing(t.id, CrossDirection.OUT)
                }
            }
            t.prevSide = zone
        }
        return crossings
    }

    /**
     * Remembers the boardings of tracks the tracker has just dropped while they can still
     * be taken back.
     *
     * A person close to the camera is often lost for a moment. The next track to appear
     * near where they were lost is taken as the same person (see [process]), so stepping
     * back out still takes the boarding back.
     */
    fun holdDropped(dropped: List<PersonTracker.Track>, nowMs: Long) {
        for (t in dropped) if (undoable(t, nowMs)) held += Held(t.cx, t.cy, t.countedAt)
    }
}
