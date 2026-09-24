package com.routesync.cameracount.camera

import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/**
 * Tracker and line together, fed frame by frame as CameraScreen feeds them. Each test is
 * something that happens at a bus door, and the assertion is what the count should do.
 */
@RunWith(RobolectricTestRunner::class)
class BoardingScenarioTest {

    private val tracker = PersonTracker()
    private val line = LineCrossCounter()

    private fun play(frames: List<List<YoloDetector.Det>>) = tracker.run(line, frames)

    @Test
    fun `one passenger boards`() {
        val crossings = play(oneWalking(walk(0.9f, 0.1f)))

        assertEquals(1, crossings.boardings)
        assertEquals(0, crossings.exits)
    }

    @Test
    fun `one passenger alights`() {
        val crossings = play(oneWalking(walk(0.1f, 0.9f)))

        assertEquals(0, crossings.boardings)
        assertEquals(1, crossings.exits)
    }

    @Test
    fun `two passengers boarding side by side count as two`() {
        val frames = walk(0.9f, 0.1f).map { listOf(person(it, 0.25f), person(it, 0.75f)) }

        val crossings = play(frames)

        assertEquals(2, crossings.boardings)
        assertEquals(2, crossings.map { it.trackId }.distinct().size)
    }

    @Test
    fun `passengers boarding one after another each count`() {
        val frames = List(3) { oneWalking(walk(0.9f, 0.1f)) + nobody(15) }.flatten()

        assertEquals(3, play(frames).boardings)
    }

    @Test
    fun `a passenger who steps on and straight back off leaves a boarding and an exit`() {
        val crossings = play(oneWalking(walk(0.9f, 0.2f) + walk(0.2f, 0.9f)))

        assertEquals(1, crossings.boardings)
        assertEquals(1, crossings.exits)
    }

    @Test
    fun `a passenger half-hidden while crossing is still counted once`() {
        // The door frame drops the detector's confidence below what starts a track, right
        // as the person goes through.
        val approach = walk(0.9f, 0.65f).map { listOf(person(it)) }
        val occluded = walk(0.6f, 0.35f).map { listOf(person(it, score = LOW)) }
        val inside = walk(0.3f, 0.1f).map { listOf(person(it)) }

        val crossings = play(approach + occluded + inside)

        assertEquals(1, crossings.boardings)
    }

    @Test
    fun `a passenger missed for a couple of frames mid-crossing is counted once`() {
        // Unseen at 0.55, 0.5 and 0.45: last seen outside the line, next seen inside it.
        val frames = walk(0.9f, 0.1f).mapIndexed { i, x ->
            if (i in 7..9) emptyList() else listOf(person(x))
        }

        val crossings = play(frames)

        assertEquals(1, crossings.boardings)
        assertEquals(1, crossings.map { it.trackId }.distinct().size)
    }

    @Test
    fun `a boarded passenger lost and found again inside is not counted twice`() {
        // Out of sight long enough to lose the track, then back on the inward side under
        // a new identity, and milling about over the line.
        val boarding = oneWalking(walk(0.9f, 0.2f))
        val milling = oneWalking(List(3) { 0.2f } + walk(0.2f, 0.7f) + walk(0.7f, 0.2f))

        val crossings = play(boarding + nobody(20) + milling)

        assertEquals(1, crossings.boardings)
    }

    @Test
    fun `the driver standing by the line is never counted`() {
        val sway = List(100) { if (it % 10 < 5) 0.44f else 0.4f }

        assertEquals(emptyList<Crossing>(), play(oneWalking(sway)))
    }

    @Test
    fun `someone brushing past before confirmation is not counted`() {
        // Two frames is below the tracker's confirmation threshold.
        val crossings = play(oneWalking(listOf(0.6f, 0.4f)) + nobody(20))

        assertEquals(emptyList<Crossing>(), crossings)
    }

    @Test
    fun `moving the line under a standing person counts nobody once history is reset`() {
        play(oneWalking(List(5) { 0.6f }))

        // The line is dragged past them, which puts them on its inward side.
        line.ax = 0.7f; line.bx = 0.7f
        tracker.resetCrossingState()

        assertEquals(emptyList<Crossing>(), play(oneWalking(List(5) { 0.6f })))
    }

    @Test
    fun `moving the line without resetting history would count a phantom boarding`() {
        // The reason CameraScreen resets on every line change.
        play(oneWalking(List(5) { 0.6f }))

        line.ax = 0.7f; line.bx = 0.7f

        assertEquals(1, play(oneWalking(List(5) { 0.6f })).boardings)
    }
}
