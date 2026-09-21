-- One row per health event the counter phone reports about itself.
--
-- Background
--
-- The counter phone is a mounted Android device that counts passengers for a whole shift
-- with nobody holding it and nobody watching the screen. Reliability testing has to
-- answer two questions about that arrangement: how often the application stops running
-- during a shift, and how much battery an hour of continuous counting costs.
--
-- Neither question can be answered from what the system already stores. device_status
-- holds a heartbeat that is rewritten in place and keeps no history, so it can say
-- whether a phone is alive now and nothing about the night it was not. trips holds the
-- counts, which describe the passengers rather than the machine that counted them.
--
-- Model
--
-- Two event types are recorded, and only two. 'restart' is written when the application
-- starts. 'battery_reading' is written from time to time with the charge level at that
-- moment.
--
-- Crashes and freezes are deliberately not among them. An application that has crashed
-- cannot write a row describing its own crash, and a frozen one writes nothing at all,
-- which is what being frozen means. A crash column and a freeze column would therefore
-- stand empty after every shift, and an empty column is not evidence that a device ran
-- cleanly. It is evidence of nothing, which is worse than no column, because a reader
-- will take it for the former.
--
-- Both faults are derived instead, from traces the device leaves while it is still
-- healthy enough to leave them:
--
--   A freeze appears as a gap in device_status.last_seen. The phone stamps that column
--   every few seconds while it runs, so a stamp that stops and later resumes without the
--   application having started again is a freeze, and the width of the gap is how long
--   it lasted.
--
--   A crash appears as a 'restart' row with no clean shutdown behind it. A restart that
--   was expected, such as a phone switched off between shifts or updated at the
--   terminal, is accounted for by the observer's note against it. A restart in the
--   middle of a run with no such account is the application having died.
--
-- Battery consumption per hour is the slope between battery_reading rows from one device
-- across one run. Two readings are enough to produce a figure and more make it steadier,
-- so the phone may report as often as is convenient without changing the shape of this
-- table.
--
-- Nothing here is written by a person. What an observer notices, a marker that stopped
-- moving or a screen that went black, is kept on the field record they are already
-- filling in, and this table stays what the device itself can vouch for. A free text
-- column would invite one kind of evidence to be filed as the other.
--
-- Device attribution
--
-- device_id carries no foreign key, for the same reason boarding_events.counter_device_id
-- carries none. There is no table of devices. device_status holds a heartbeat that is
-- rewritten every few seconds, and vehicles.counter_device_id holds a binding that
-- changes whenever a phone is swapped between buses. Neither can be asked later which
-- phone reported a given event, so the row has to name the device itself.
--
-- trip_id is left nullable. A phone that restarts between runs, or reports its charge
-- while the bus waits at the terminal, is still reporting something a reliability figure
-- needs. The foreign key applies when a trip is named and is silent when one is not.

begin;

create table if not exists public.device_health_log (
  log_id        bigint      generated always as identity,
  device_id     text        not null,
  trip_id       character varying(20),
  event_type    text        not null,
  battery_level integer,
  occurred_at   timestamptz not null default now(),

  constraint pk_device_health_log primary key (log_id),
  constraint fk_device_health_log_trip
    foreign key (trip_id) references public.trips (trip_id),
  constraint ck_device_health_log_event_type
    check (event_type in ('restart', 'battery_reading')),
  constraint ck_device_health_log_battery_level
    check (battery_level is null or battery_level between 0 and 100)
);

comment on table public.device_health_log is 'What the counter phone reports about its own running. Restarts and charge levels only, because a crashed or frozen application cannot report itself.';

comment on column public.device_health_log.device_id is 'The phone that reported it, as it identified itself then. Deliberately not a foreign key: no table of devices exists, and the vehicle binding it would point at changes when a phone is swapped.';

comment on column public.device_health_log.trip_id is 'The run this happened during, when there was one. Null for a restart or a reading outside a trip, which is still worth recording.';

comment on column public.device_health_log.event_type is 'restart when the application started, battery_reading for a charge level. A crash is read as a restart with no clean shutdown behind it, and a freeze as a gap in device_status.last_seen.';

comment on column public.device_health_log.battery_level is 'Charge percentage at the moment of the reading. Null on a restart row, which reports an event rather than a level.';

comment on column public.device_health_log.occurred_at is 'When the event happened. Stored as a real instant, read in Philippine time through device_health_log_ph.';

-- Both questions this table exists for are asked of one device over one stretch of time:
-- the restarts within a shift, and the charge readings to draw a slope through.
create index if not exists ix_device_health_log_device_occurred
  on public.device_health_log (device_id, occurred_at);

-- ---------------------------------------------------------------------------
-- Reading
-- ---------------------------------------------------------------------------

-- Stored as a real instant, because occurred_at comes from now() and a duration measured
-- against a naked local time crosses a day boundary wrongly. Read in Philippine time,
-- because every operator-facing date in this system is, and a reliability result nobody
-- can line up against a shift is not a result.
create or replace view public.device_health_log_ph as
select
  log_id,
  device_id,
  trip_id,
  event_type,
  battery_level,
  occurred_at at time zone 'Asia/Manila' as occurred_ph
from public.device_health_log;

comment on view public.device_health_log_ph is 'device_health_log in Philippine wall-clock time, so a restart can be placed against the shift it interrupted.';

-- ---------------------------------------------------------------------------
-- Access
-- ---------------------------------------------------------------------------

alter table public.device_health_log enable row level security;

-- The counter phone writes; nothing else does. No update and no delete for any device
-- role: a health event is a record of something that happened, not a piece of state, and
-- a device that could revise its own reliability record would be reporting on itself
-- twice. The service key is unaffected and remains the way a researcher adds a note.
grant insert on public.device_health_log to app_camera;

-- A device may only write events under its own identity. The trip is left to the foreign
-- key, which refuses an event naming a trip that does not exist.
--
-- The policy deliberately does not require the device to still hold the bus. A phone
-- that comes back after a long outage reports the restart that ended it, by which time
-- the trip has closed and the binding may have moved, and refusing those is refusing
-- exactly the failures this table exists to count.
drop policy if exists p_device_health_insert_own_device on public.device_health_log;
create policy p_device_health_insert_own_device on public.device_health_log
  for insert to app_camera
  with check (
    device_id = nullif(current_setting('request.jwt.claims', true), '')::json->>'device_id'
  );

commit;
