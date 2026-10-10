-- Checks 2026-10-10-telemetry-indexes.sql. Run it after the migration, in the Supabase
-- SQL editor. A failure raises; a pass prints one notice.

do $test$
begin
  if not exists (select 1 from pg_indexes
                 where schemaname = 'public' and tablename = 'telemetry_data'
                   and indexname = 'idx_telemetry_trip_time'
                   and indexdef like '%(trip_id, "timestamp")%') then
    raise exception 'idx_telemetry_trip_time is missing or on the wrong columns';
  end if;
  if not exists (select 1 from pg_indexes
                 where schemaname = 'public' and tablename = 'telemetry_data'
                   and indexname = 'idx_telemetry_time'
                   and indexdef like '%("timestamp")%') then
    raise exception 'idx_telemetry_time is missing or on the wrong column';
  end if;
  if exists (select 1 from pg_index i join pg_class c on c.oid = i.indexrelid
             where c.relname in ('idx_telemetry_trip_time', 'idx_telemetry_time')
               and not i.indisvalid) then
    raise exception 'a telemetry index is invalid; drop it and create it again';
  end if;
  raise notice 'pass: telemetry_data is indexed by trip and time, and by time';
end
$test$;
