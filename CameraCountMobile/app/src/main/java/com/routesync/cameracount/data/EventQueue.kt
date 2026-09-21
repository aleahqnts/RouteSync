package com.routesync.cameracount.data

import android.content.Context
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase

/**
 * One detected crossing, waiting to reach the database.
 *
 * A crossing is written here the moment it is detected, before anything is sent. The bus
 * spends much of its route without a usable signal, and a crossing that exists only in
 * memory is lost to a process kill, a reboot or a flat battery. The count survives those
 * already; the evidence behind it has to survive them too, or an accuracy run turns into
 * a measurement of network coverage.
 *
 * [eventId] is made on the device and is the primary key both here and in the database,
 * so a resend after a lost reply stores once rather than twice.
 *
 * [tripId] is stamped at the crossing rather than resolved on delivery. A phone out of
 * contact across a shift change keeps counting into the trip it last saw, and its events
 * have to say the same thing its count says.
 *
 * [attempts] counts deliveries the server refused outright, as opposed to ones that never
 * got through. Only the former are a property of the row.
 */
@Entity(tableName = "boarding_event_queue")
data class QueuedEvent(
    @PrimaryKey val eventId: String,
    val tripId: String,
    val deviceId: String,
    /** `in` or `out`, matching the check constraint on the table this lands in. */
    val direction: String,
    /** When the crossing happened, in epoch milliseconds on the device clock. */
    val deviceTimestamp: Long,
    val attempts: Int = 0
)

@Dao
interface EventQueueDao {

    /**
     * Records a crossing.
     *
     * A collision on the identifier is ignored rather than replacing the row, because
     * the only way one happens is a redelivery of something already held, and the held
     * copy already carries its attempt history.
     */
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun enqueue(event: QueuedEvent)

    /** The oldest waiting events, which is the order they are delivered in. */
    @Query("select * from boarding_event_queue order by deviceTimestamp, eventId limit :limit")
    suspend fun oldest(limit: Int): List<QueuedEvent>

    @Query("delete from boarding_event_queue where eventId in (:ids)")
    suspend fun forget(ids: List<String>)

    @Query("update boarding_event_queue set attempts = attempts + 1 where eventId in (:ids)")
    suspend fun noteRefused(ids: List<String>)

    /**
     * Gives up on events the server will not take and on ones too old to matter, and
     * reports how many were dropped so the decision can be logged rather than made
     * silently.
     */
    @Query(
        "delete from boarding_event_queue " +
            "where attempts >= :maxAttempts or deviceTimestamp < :oldestKept"
    )
    suspend fun purge(maxAttempts: Int, oldestKept: Long): Int

    @Query("select count(*) from boarding_event_queue")
    suspend fun depth(): Int
}

/**
 * The device's own store, holding nothing but the crossing queue.
 *
 * Separate from DataStore on purpose. DataStore rewrites its whole file on every commit,
 * which suits the handful of settings and the single held count it already carries and
 * does not suit thousands of rows appended one at a time.
 */
@Database(entities = [QueuedEvent::class], version = 1, exportSchema = false)
abstract class EventDb : RoomDatabase() {

    abstract fun events(): EventQueueDao

    companion object {
        @Volatile private var instance: EventDb? = null

        fun get(context: Context): EventDb = instance ?: synchronized(this) {
            instance ?: Room.databaseBuilder(
                context.applicationContext, EventDb::class.java, "cameracount-events"
            )
                // A schema change here would be a change to what an event is, which is a
                // decision to make deliberately rather than by dropping the queue on an
                // upgrade. Nothing is migrated because nothing has changed yet.
                .build()
                .also { instance = it }
        }
    }
}
