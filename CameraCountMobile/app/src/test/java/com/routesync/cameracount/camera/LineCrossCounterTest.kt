package com.routesync.cameracount.camera

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/**
 * The line on its own, driven by tracks placed by hand so each rule can be isolated
 * from the tracker's association and confirmation.
 */
@RunWith(RobolectricTestRunner::class)
class LineCrossCounterTest {

    private fun track(cx: Float, cy: Float = 0.5f, id: Int = 1) =
        PersonTracker.Track(id, personBox(cx, cy), 0.9f)

    /** Moves [t] through [xs] (and [ys], if given) and returns every crossing reported. */
    private fun LineCrossCounter.follow(
        t: PersonTracker.Track,
        xs: List<Float>,
        ys: List<Float> = List(xs.size) { 0.5f }
    ): List<Crossing> = xs.zip(ys).flatMap { (x, y) ->
        t.box = personBox(x, y)
        process(listOf(t))
    }

    @Test
    fun `a walk from the outward side across the line is one boarding`() {
        val counter = LineCrossCounter()
        val t = track(0.8f, id = 7)

        val crossings = counter.follow(t, walk(0.8f, 0.2f))

        assertEquals(listOf(Crossing(7, CrossDirection.IN)), crossings)
        assertTrue(t.counted)
        assertFalse(t.exited)
    }

    @Test
    fun `a walk the other way is one exit and no boarding`() {
        val counter = LineCrossCounter()
        val t = track(0.2f, id = 3)

        val crossings = counter.follow(t, walk(0.2f, 0.8f))

        assertEquals(listOf(Crossing(3, CrossDirection.OUT)), crossings)
        assertFalse(t.counted)
    }

    @Test
    fun `someone first seen inside never boards, even after stepping out and back`() {
        // The driver, or a passenger already aboard, wandering over the line.
        val counter = LineCrossCounter()
        val t = track(0.3f)

        val crossings = counter.follow(t, walk(0.3f, 0.7f) + walk(0.7f, 0.3f))

        assertEquals(0, crossings.boardings)
        assertEquals(1, crossings.exits)
    }

    @Test
    fun `a boarding that is withdrawn reports both directions once`() {
        val counter = LineCrossCounter()
        val t = track(0.8f, id = 5)

        val crossings = counter.follow(t, walk(0.8f, 0.2f) + walk(0.2f, 0.8f))

        assertEquals(
            listOf(Crossing(5, CrossDirection.IN), Crossing(5, CrossDirection.OUT)),
            crossings
        )
    }

    @Test
    fun `loitering back and forth over the line reports one pair, not a stream`() {
        val counter = LineCrossCounter()
        val t = track(0.8f)
        val pacing = walk(0.8f, 0.4f) + List(10) { listOf(0.6f, 0.4f) }.flatten()

        val crossings = counter.follow(t, pacing)

        assertEquals(1, crossings.boardings)
        assertEquals(1, crossings.exits)
    }

    @Test
    fun `movement inside the dead band never registers`() {
        // A hand hovering over the line. The band is 0.02 either side of it.
        val counter = LineCrossCounter()
        val t = track(0.515f)
        val jitter = List(50) { if (it % 2 == 0) 0.515f else 0.485f }

        assertEquals(emptyList<Crossing>(), counter.follow(t, jitter))
        assertEquals(0, t.prevSide)
    }

    @Test
    fun `a side is only entered once the dead band is cleared`() {
        val counter = LineCrossCounter()
        val t = track(0.8f)

        // 0.49 is on the inward side of the line but inside the band: nothing yet.
        assertEquals(emptyList<Crossing>(), counter.follow(t, listOf(0.8f, 0.6f, 0.49f)))
        assertEquals(-1, t.prevSide)

        assertEquals(1, counter.follow(t, listOf(0.45f)).boardings)
    }

    @Test
    fun `a track first seen on the line takes its origin from the first side it clears`() {
        val counter = LineCrossCounter()
        val t = track(0.5f)

        val crossings = counter.follow(t, listOf(0.5f, 0.5f, 0.6f, 0.4f))

        assertEquals(-1, t.originSide)
        assertEquals(1, crossings.boardings)
    }

    @Test
    fun `flipping the inward sign swaps which way is boarding`() {
        val counter = LineCrossCounter(inwardSign = -1)

        val leftward = counter.follow(track(0.8f, id = 1), walk(0.8f, 0.2f))
        val rightward = counter.follow(track(0.2f, id = 2), walk(0.2f, 0.8f))

        assertEquals(listOf(Crossing(1, CrossDirection.OUT)), leftward)
        assertEquals(listOf(Crossing(2, CrossDirection.IN)), rightward)
    }

    @Test
    fun `an angled line counts a crossing through it`() {
        // Diagonal from top left to bottom right. With the default sign the inward side
        // is below it, where y is greater than x.
        val counter = LineCrossCounter(ax = 0.2f, ay = 0.2f, bx = 0.8f, by = 0.8f)
        val t = track(0.7f, 0.3f)

        val crossings = counter.follow(t, walk(0.7f, 0.3f), walk(0.3f, 0.7f))

        assertEquals(1, crossings.boardings)
        assertEquals(0, crossings.exits)
    }

    @Test
    fun `an angled line ignores movement parallel to it`() {
        val counter = LineCrossCounter(ax = 0.2f, ay = 0.2f, bx = 0.8f, by = 0.8f)
        // Just below the diagonal and sliding along it.
        val xs = walk(0.2f, 0.6f)
        val ys = xs.map { it + 0.1f }

        assertEquals(emptyList<Crossing>(), counter.follow(track(xs[0], ys[0]), xs, ys))
    }

    @Test
    fun `each track in a frame is judged on its own`() {
        val counter = LineCrossCounter()
        val boarding = track(0.8f, 0.25f, id = 1)
        val aboard = track(0.2f, 0.75f, id = 2)

        val crossings = walk(0.8f, 0.2f).flatMap { x ->
            boarding.box = personBox(x, 0.25f)
            counter.process(listOf(boarding, aboard))
        }

        assertEquals(listOf(Crossing(1, CrossDirection.IN)), crossings)
    }
}
