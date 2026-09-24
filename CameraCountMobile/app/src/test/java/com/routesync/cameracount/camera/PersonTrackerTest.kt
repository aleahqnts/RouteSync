package com.routesync.cameracount.camera

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/** The tracker on its own: which detections become tracks, and when identity is kept. */
@RunWith(RobolectricTestRunner::class)
class PersonTrackerTest {

    private fun PersonTracker.feed(frames: List<List<YoloDetector.Det>>) =
        frames.map { update(it) }

    @Test
    fun `a track is reported only from its third consecutive frame`() {
        val tracker = PersonTracker()

        val seen = tracker.feed(List(3) { listOf(person(0.5f)) })

        assertEquals(listOf(0, 0, 1), seen.map { it.size })
    }

    @Test
    fun `a single-frame artefact never becomes a track`() {
        val tracker = PersonTracker()

        val seen = tracker.feed(listOf(listOf(person(0.5f))) + nobody(20))

        assertTrue(seen.all { it.isEmpty() })
    }

    @Test
    fun `low-confidence detections never start a track`() {
        val tracker = PersonTracker()

        val seen = tracker.feed(List(20) { listOf(person(0.5f, score = LOW)) })

        assertTrue(seen.all { it.isEmpty() })
    }

    @Test
    fun `a person walking keeps one identity`() {
        val tracker = PersonTracker()

        val ids = tracker.feed(oneWalking(walk(0.9f, 0.1f)))
            .flatten().map { it.id }.distinct()

        assertEquals(1, ids.size)
    }

    @Test
    fun `low-confidence detections keep an existing track alive`() {
        // Someone half-hidden by the door frame drops below HIGH_CONF. The second
        // association stage must hold them on the identity they already have.
        val tracker = PersonTracker()
        val before = tracker.feed(oneWalking(walk(0.9f, 0.7f))).last().single()

        val during = tracker.feed(walk(0.65f, 0.3f).map { listOf(person(it, score = LOW)) })

        assertTrue(during.all { it.size == 1 && it.single().id == before.id })
    }

    @Test
    fun `a track is withheld while unseen and resumes if seen again in time`() {
        val tracker = PersonTracker()
        val id = tracker.feed(List(3) { listOf(person(0.5f)) }).last().single().id

        val gap = tracker.feed(nobody(12))
        val back = tracker.update(listOf(person(0.5f)))

        assertTrue(gap.all { it.isEmpty() })
        assertEquals(listOf(id), back.map { it.id })
    }

    @Test
    fun `a track unseen for too long is dropped and a return starts afresh`() {
        val tracker = PersonTracker()
        val id = tracker.feed(List(3) { listOf(person(0.5f)) }).last().single().id

        tracker.feed(nobody(13))
        val back = tracker.feed(List(3) { listOf(person(0.5f)) })

        // A new track has to earn confirmation again, under a new identity.
        assertEquals(listOf(0, 0, 1), back.map { it.size })
        assertNotEquals(id, back.last().single().id)
    }

    @Test
    fun `two people apart are two tracks`() {
        val tracker = PersonTracker()

        val seen = tracker.feed(List(3) { listOf(person(0.3f, 0.25f), person(0.7f, 0.75f)) })

        assertEquals(2, seen.last().map { it.id }.distinct().size)
    }

    @Test
    fun `a detection that does not overlap a track is not taken as that track`() {
        val tracker = PersonTracker()
        val first = tracker.feed(List(3) { listOf(person(0.2f)) }).last().single()

        // The first person vanishes the same frame someone appears across the frame.
        val seen = tracker.feed(List(3) { listOf(person(0.8f)) })

        assertEquals(1, seen.last().size)
        assertNotEquals(first.id, seen.last().single().id)
    }

    @Test
    fun `resetting crossing state keeps whether a track was already recorded`() {
        val tracker = PersonTracker()
        val t = tracker.feed(List(3) { listOf(person(0.5f)) }).last().single()
        t.prevSide = 1
        t.originSide = -1
        t.counted = true
        t.exited = true

        tracker.resetCrossingState()

        assertEquals(0, t.prevSide)
        assertEquals(0, t.originSide)
        assertTrue(t.counted)
        assertTrue(t.exited)
    }
}
