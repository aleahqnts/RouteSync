-- Records when the server accepted a telemetry reading, alongside when the device took it.
--
-- Background
--
-- telemetry_data.timestamp is already the moment the fix was taken, written on the phone
-- by the driver app and carried unchanged through its offline queue. It is not an
-- ingestion time and has no default.
--
-- That is the half worth keeping, and it is the half a staleness reading needs: how old
-- the displayed position is can only be answered against when the position was taken. An
-- ingestion time in its place would measure the age of the insert, which is near zero
-- whether the reading is fresh or three hours late out of a queue.
--
-- What is missing is the other half. With only one time on the row there is no way to
-- separate a reading that was slow to arrive from one that was slow to be taken, so a
-- gap in the map cannot be attributed to the device, the network, or the server.
--
-- Model
--
-- The existing column keeps its name and its meaning. A second column records arrival,
-- defaulted by the database so no client has to send it and no old build has to change.
-- Rows written before this migration carry their own timestamp as the arrival time,
-- which is the closest true statement available and keeps the column honest about never
-- being null rather than leaving a hole readers have to special-case.

begin;

alter table public.telemetry_data
  add column if not exists received_at timestamptz;

comment on column public.telemetry_data."timestamp" is 'When the fix was taken, by the device clock. Written by the driver app and carried unchanged through its offline queue, so a reading held in a dead zone keeps the time it was actually taken.';

comment on column public.telemetry_data.received_at is 'When the database accepted the row. With timestamp it separates a reading that was slow to arrive from one that was slow to be taken.';

-- Backfilled from the only time those rows have. It understates nothing: a row that was
-- queued arrived later than this says, and no reading arrived before it was taken.
update public.telemetry_data
   set received_at = "timestamp"
 where received_at is null;

alter table public.telemetry_data
  alter column received_at set default now(),
  alter column received_at set not null;

commit;
