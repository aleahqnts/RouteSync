


SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;


COMMENT ON SCHEMA "public" IS 'standard public schema';



CREATE EXTENSION IF NOT EXISTS "pg_stat_statements" WITH SCHEMA "extensions";






CREATE EXTENSION IF NOT EXISTS "pgcrypto" WITH SCHEMA "extensions";






CREATE EXTENSION IF NOT EXISTS "supabase_vault" WITH SCHEMA "vault";






CREATE EXTENSION IF NOT EXISTS "uuid-ossp" WITH SCHEMA "extensions";






CREATE TYPE "public"."account_status_enum" AS ENUM (
    'Activated',
    'Deactivated'
);


ALTER TYPE "public"."account_status_enum" OWNER TO "postgres";


CREATE TYPE "public"."checklist_status_enum" AS ENUM (
    'Passed',
    'Failed',
    'Pending',
    'Passed with Defects',
    'Skipped'
);


ALTER TYPE "public"."checklist_status_enum" OWNER TO "postgres";


COMMENT ON TYPE "public"."checklist_status_enum" IS 'Outcome of a bus inspection. Passed and Passed with Defects mean the bus was inspected and may be driven, Failed means it was inspected and grounded, Pending means the row exists but no outcome has been recorded yet, and Skipped means no inspection was carried out because an earlier driver had already inspected the bus that operational day.';



CREATE TYPE "public"."maintenance_status_enum" AS ENUM (
    'Needs Attention',
    'Under Repair',
    'No Issues'
);


ALTER TYPE "public"."maintenance_status_enum" OWNER TO "postgres";


CREATE TYPE "public"."priority_enum" AS ENUM (
    'Normal',
    'High',
    'Urgent'
);


ALTER TYPE "public"."priority_enum" OWNER TO "postgres";


CREATE TYPE "public"."target_audience_enum" AS ENUM (
    'All',
    'Route',
    'Driver'
);


ALTER TYPE "public"."target_audience_enum" OWNER TO "postgres";


CREATE TYPE "public"."trip_status_enum" AS ENUM (
    'Not Yet Started',
    'Active',
    'Completed',
    'Assignment Issue',
    'Pending'
);


ALTER TYPE "public"."trip_status_enum" OWNER TO "postgres";


CREATE TYPE "public"."vehicle_status_enum" AS ENUM (
    'Ready to Deploy',
    'Flagged',
    'Pending',
    'On Trip'
);


ALTER TYPE "public"."vehicle_status_enum" OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."audit_log_immutable"() RETURNS "trigger"
    LANGUAGE "plpgsql"
    AS $$
begin
  raise exception 'audit_log is append-only: % is not permitted', TG_OP
    using hint = 'History cannot be rewritten or trimmed. Removing this guard '
               || 'requires explicitly dropping/disabling the trigger, which is '
               || 'itself a deliberate, visible act.';
end $$;


ALTER FUNCTION "public"."audit_log_immutable"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."audit_password_change"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
  v_claims json;
  v_role   text;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');
  insert into public.audit_log
    (actor_type, actor_id, actor_role, action, target_table, target_id, source, outcome, summary)
  values
    (case v_role when 'app_driver' then 'user' when 'service_role' then 'admin' else 'system' end,
     v_claims->>'user_id', v_role, 'password_hash_changed', 'users', new.user_id::text,
     'db', 'ok', 'Password hash changed for user ' || new.user_id);
  return new;
end $$;


ALTER FUNCTION "public"."audit_password_change"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."audit_row_change"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
  v_claims json;
  v_role   text;
  v_actor_type text;
  v_actor_id   text;
  v_old jsonb;
  v_new jsonb;
  v_pk  text;
  v_summary text;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');

  v_actor_type := case v_role
    when 'app_driver'   then 'user'
    when 'app_camera'   then 'device'
    when 'service_role' then 'admin'   -- web/edge via service key (10b adds the admin name)
    when 'anon'         then 'anon'
    else 'system'
  end;
  v_actor_id := coalesce(v_claims->>'user_id', v_claims->>'device_id');

  if TG_OP <> 'INSERT' then v_old := to_jsonb(OLD) - 'password_hash'; end if;
  if TG_OP <> 'DELETE' then v_new := to_jsonb(NEW) - 'password_hash'; end if;

  v_pk := coalesce(v_new->>'user_id', v_old->>'user_id',
                   v_new->>'device_id', v_old->>'device_id');

  -- Human line. device_config gets a purpose-built one: "Update on device_config"
  -- means nothing to an auditor, but "counting line changed" is the event that
  -- moves passenger counts (and therefore revenue figures).
  if TG_TABLE_NAME = 'device_config' then
    v_summary := 'Camera ' || coalesce(v_pk, '?') || ': '
      || case
           when TG_OP = 'INSERT' then 'config created'
           when TG_OP = 'DELETE' then 'config deleted'
           when (v_old->>'line_ax') is distinct from (v_new->>'line_ax')
             or (v_old->>'line_ay') is distinct from (v_new->>'line_ay')
             or (v_old->>'line_bx') is distinct from (v_new->>'line_bx')
             or (v_old->>'line_by') is distinct from (v_new->>'line_by')
             then 'counting line moved'
           when (v_old->>'inward_sign') is distinct from (v_new->>'inward_sign')
             then 'boarding side flipped'
           when (v_old->>'use_back_camera') is distinct from (v_new->>'use_back_camera')
             then 'lens switched'
           else 'config changed'
         end
      || ' (v' || coalesce(v_new->>'version', v_old->>'version', '?')
      || ', by ' || coalesce(v_new->>'updated_by', 'unknown') || ')';
  else
    v_summary := initcap(TG_OP) || ' on ' || TG_TABLE_NAME || ' ' || coalesce(v_pk, '?')
      || case when v_actor_id is not null
              then ' by ' || v_actor_type || ' ' || v_actor_id else '' end;
  end if;

  insert into public.audit_log
    (actor_type, actor_id, actor_role, action, target_table, target_id,
     source, outcome, summary, changes)
  values
    (v_actor_type, v_actor_id, v_role, lower(TG_OP), TG_TABLE_NAME, v_pk,
     'db', 'ok', v_summary,
     jsonb_strip_nulls(jsonb_build_object('old', v_old, 'new', v_new)));

  if TG_OP = 'DELETE' then return OLD; end if;
  return NEW;
end $$;


ALTER FUNCTION "public"."audit_row_change"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."boardings_by_hour"("p_trip_ids" "text"[]) RETURNS TABLE("trip_id" "text", "hour_start" timestamp with time zone, "boarded" integer)
    LANGUAGE "sql" STABLE
    SET "search_path" TO 'public'
    AS $$
  select e.trip_id::text,
         date_bin(interval '1 hour', e.device_timestamp, timestamptz '2000-01-01 00:00:00+00'),
         count(*)::integer
  from public.boarding_events e
  where e.trip_id = any (p_trip_ids)
    and e.direction = 'in'
  group by 1, 2
  order by 1, 2
$$;


ALTER FUNCTION "public"."boardings_by_hour"("p_trip_ids" "text"[]) OWNER TO "postgres";


COMMENT ON FUNCTION "public"."boardings_by_hour"("p_trip_ids" "text"[]) IS 'Boardings per trip per hour, for the given trips. Only crossings inward are counted. Hours are whole hours, as instants.';



CREATE OR REPLACE FUNCTION "public"."camera_vehicle"() RETURNS "text"
    LANGUAGE "sql" STABLE SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
  select vehicle_id from vehicles where counter_device_id = public.jwt_dev() limit 1
$$;


ALTER FUNCTION "public"."camera_vehicle"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."dashboard_figures"("p_today" "date", "p_yesterday_active_only" boolean DEFAULT false) RETURNS json
    LANGUAGE "sql" STABLE
    SET "search_path" TO 'public'
    AS $$
  select json_build_object(
    'open_logs', coalesce((select json_agg(x) from (
        select log_id, vehicle_id from maintenance_logs
        where resolved_at is null) x), '[]'),
    'today_trips', coalesce((select json_agg(x) from (
        select trip_id, date, route_id, vehicle_id, shift_type, shift_start_time, shift_end_time,
               trip_status, estimated_revenue, total_boarded, actual_end_time
        from trips where date = p_today) x), '[]'),
    'yesterday_trips', coalesce((select json_agg(x) from (
        select trip_id, date, route_id, vehicle_id, shift_type, shift_start_time, shift_end_time,
               trip_status, estimated_revenue, total_boarded, actual_end_time
        from trips
        where date = p_today - 1
          and (not p_yesterday_active_only or trip_status = 'Active')) x), '[]'),
    'routes', coalesce((select json_agg(x order by x.route_name) from (
        select route_id, route_name from routes) x), '[]'))
$$;


ALTER FUNCTION "public"."dashboard_figures"("p_today" "date", "p_yesterday_active_only" boolean) OWNER TO "postgres";


COMMENT ON FUNCTION "public"."dashboard_figures"("p_today" "date", "p_yesterday_active_only" boolean) IS 'The rows the dashboard''s cards are worked out from, in one request. With p_yesterday_active_only, only yesterday''s trips still running.';



CREATE OR REPLACE FUNCTION "public"."driver_active_camera"() RETURNS "text"
    LANGUAGE "sql" STABLE SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
  select v.counter_device_id
  from trips t
  join vehicles v on v.vehicle_id = t.vehicle_id
  where t.driver_id = public.jwt_uid()
    and t.trip_status = 'Active'
    and v.counter_device_id is not null
  limit 1
$$;


ALTER FUNCTION "public"."driver_active_camera"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."driver_is_active"() RETURNS boolean
    LANGUAGE "sql" STABLE SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
  select exists (
    select 1 from users
    where user_id = public.jwt_uid() and account_status = 'Activated'
  )
$$;


ALTER FUNCTION "public"."driver_is_active"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."fleetmap_live"("p_op_day" "date", "p_route_id" integer, "p_since" timestamp with time zone, "p_seen" "jsonb" DEFAULT '{}'::"jsonb") RETURNS json
    LANGUAGE "sql" STABLE
    SET "search_path" TO 'public'
    AS $$
  with active as (
    select trip_id, date, route_id, vehicle_id, driver_id, shift_start_time, shift_end_time,
           trip_status, actual_start_time, total_boarded
    from trips where trip_status = 'Active'
  ), shown as (
    select a.trip_id from active a
    where (p_route_id is null or a.route_id = p_route_id)
      and (a.date = p_op_day or (a.date = p_op_day - 1 and a.actual_start_time is not null))
  )
  select json_build_object(
    'trips', coalesce((select json_agg(a) from active a), '[]'),
    'telemetry', coalesce((select json_agg(x order by x."timestamp" desc) from (
        select t.telemetry_id, t.trip_id, t.latitude, t.longitude, t.total_passengers,
               t.speed, t.heading, t.accuracy, t."timestamp"
        from telemetry_data t
        where t.trip_id in (select trip_id from shown)
          and t."timestamp" >= p_since
          and t.telemetry_id >= coalesce((p_seen ->> t.trip_id)::bigint, 0)
        order by t."timestamp" desc
        limit 1000) x), '[]'),
    'fare', coalesce((select json_agg(f) from fare_config f), '[]'))
$$;


ALTER FUNCTION "public"."fleetmap_live"("p_op_day" "date", "p_route_id" integer, "p_since" timestamp with time zone, "p_seen" "jsonb") OWNER TO "postgres";


COMMENT ON FUNCTION "public"."fleetmap_live"("p_op_day" "date", "p_route_id" integer, "p_since" timestamp with time zone, "p_seen" "jsonb") IS 'What the fleet map reads on every poll, in one request: active trips, the positions of those shown from the last one the map used (p_seen, by trip), and the fare.';



CREATE OR REPLACE FUNCTION "public"."jwt_dev"() RETURNS "text"
    LANGUAGE "sql" STABLE
    AS $$
  select nullif(current_setting('request.jwt.claims', true), '')::json ->> 'device_id'
$$;


ALTER FUNCTION "public"."jwt_dev"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."jwt_uid"() RETURNS integer
    LANGUAGE "sql" STABLE
    AS $$
  select nullif(nullif(current_setting('request.jwt.claims', true), '')::json ->> 'user_id', '')::int
$$;


ALTER FUNCTION "public"."jwt_uid"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."nav_badge_inputs"("p_today" "date", "p_this_month" "date", "p_next_month" "date", "p_open_statuses" "text"[]) RETURNS json
    LANGUAGE "plpgsql" STABLE
    SET "search_path" TO 'public'
    AS $$
declare
  v_roster    json;
  v_incidents json;
begin
  begin
    v_roster := json_build_object(
      'months', coalesce((select json_agg(x) from (
          select month, status from roster_months
          where month in (p_this_month, p_next_month)) x), '[]'),
      'gaps', coalesce((select json_agg(x) from (
          select date, vehicle_id, shift from roster_gaps
          where date >= p_today + 1) x), '[]'),
      'skips', coalesce((select json_agg(x) from (
          select date, vehicle_id, shift from roster_skips
          where date >= p_today + 1) x), '[]'),
      'slots', coalesce((select json_agg(x) from (
          select month, driver_id, vehicle_id, route_id, shift, suggested from roster_slots
          where month in (p_this_month, p_next_month)) x), '[]'),
      -- Trips already running on the days that have gaps, which fill them.
      'gap_trips', coalesce((select json_agg(x) from (
          select date, vehicle_id, shift_type from trips
          where date in (select g.date from roster_gaps g where g.date >= p_today + 1)) x), '[]'),
      -- The drivers and buses placed on this month's roster, to find places that no longer hold.
      'slot_drivers', coalesce((select json_agg(x) from (
          select user_id, account_status from users
          where user_id in (select s.driver_id from roster_slots s
                            where s.month = p_this_month and s.driver_id is not null)) x), '[]'),
      'slot_buses', coalesce((select json_agg(x) from (
          select vehicle_id, retired_at, route_id from vehicles
          where vehicle_id in (select s.vehicle_id from roster_slots s
                               where s.month = p_this_month and s.vehicle_id is not null)) x), '[]'));
  exception when others then
    v_roster := null;
  end;

  begin
    v_incidents := coalesce((select json_agg(x) from (
        select severity from security_incidents
        where needs_review is true
        limit 1000) x), '[]');
  exception when others then
    v_incidents := null;
  end;

  return json_build_object(
    'trips', coalesce((select json_agg(x) from (
        select trip_id, date, vehicle_id, driver_id, trip_status, shift_start_time, shift_end_time
        from trips
        where date >= p_today and date <= p_today + 1) x), '[]'),
    'vehicles', coalesce((select json_agg(x) from (
        select vehicle_id, out_of_service, retired_at from vehicles) x), '[]'),
    'availability', coalesce((select json_agg(x) from (
        select user_id, availability_status from driver_availability) x), '[]'),
    'open_logs', coalesce((select json_agg(x) from (
        select log_id, vehicle_id from maintenance_logs
        where resolved_at is null) x), '[]'),
    'leave_open', coalesce((select json_agg(x) from (
        select request_id, user_id, status, leave_type, start_date, end_date, revoked_dates
        from leave_requests
        where status = any (p_open_statuses)) x), '[]'),
    'leave_asked', coalesce((select json_agg(x) from (
        select request_id from leave_requests
        where withdraw_requested_at is not null
          and withdraw_answered_at is null
          and status = 'Approved') x), '[]'),
    'leave_today', coalesce((select json_agg(x) from (
        select request_id, user_id, status, start_date, end_date, revoked_dates
        from leave_requests
        where status = 'Approved' and start_date <= p_today and end_date >= p_today) x), '[]'),
    'roster', v_roster,
    'incidents', v_incidents);
end
$$;


ALTER FUNCTION "public"."nav_badge_inputs"("p_today" "date", "p_this_month" "date", "p_next_month" "date", "p_open_statuses" "text"[]) OWNER TO "postgres";


COMMENT ON FUNCTION "public"."nav_badge_inputs"("p_today" "date", "p_this_month" "date", "p_next_month" "date", "p_open_statuses" "text"[]) IS 'The rows the sidebar badges are counted from, in one request. roster and incidents are null when their tables cannot be read.';



CREATE OR REPLACE FUNCTION "public"."publish_roster_month"("p_month" "date", "p_base_version" integer, "p_plan" "jsonb", "p_published_by" integer) RETURNS "jsonb"
    LANGUAGE "plpgsql"
    SET "search_path" TO 'public'
    AS $$
declare
  v_version  integer;
  v_op_day   date := ((now() at time zone 'Asia/Manila') - interval '6 hours')::date;
  v_wall     timestamptz := (now() at time zone 'Asia/Manila') at time zone 'UTC';
  v_inserted integer := 0;
  v_updated  integer := 0;
  v_deleted  integer := 0;
  v_kept     jsonb := '[]'::jsonb;
  v_dropped  integer := 0;
  v_days     date[] := '{}';
  v_clash    record;
  r          jsonb;
  t          public.trips%rowtype;
begin
  select version into v_version from roster_months where month = p_month for update;

  if not found then
    raise exception 'there is no roster for %', to_char(p_month, 'YYYY-MM') using errcode = 'RS404';
  end if;

  if v_version <> p_base_version then
    raise exception 'roster for % was saved by someone else (version % not %)',
      to_char(p_month, 'YYYY-MM'), v_version, p_base_version
      using errcode = 'RS409';
  end if;

  perform set_config('routesync.publishing', 'on', true);

  -- Updates: a new driver, or a break slot, for a trip the roster wrote.
  for r in select * from jsonb_array_elements(coalesce(p_plan -> 'updates', '[]'::jsonb)) loop
    select * into t from trips where trip_id = r ->> 'trip_id' for update;
    if not found then continue; end if;

    if t.roster_month is distinct from p_month or t.hand_edited
       or t.trip_status in ('Active', 'Completed') or t.date <= v_op_day then
      v_kept := v_kept || to_jsonb(t.trip_id);
      continue;
    end if;

    update trips
       set driver_id   = (r ->> 'driver_id')::integer,
           break_start = nullif(r ->> 'break_start', '')::time
     where trip_id = t.trip_id;

    v_updated := v_updated + 1;
    v_days := v_days || t.date;
  end loop;

  -- Deletes: a slot the roster no longer runs, or can no longer fill.
  for r in select * from jsonb_array_elements(coalesce(p_plan -> 'deletes', '[]'::jsonb)) loop
    select * into t from trips where trip_id = r #>> '{}' for update;
    if not found then continue; end if;

    if t.roster_month is distinct from p_month or t.hand_edited
       or t.trip_status in ('Active', 'Completed') or t.date <= v_op_day then
      v_kept := v_kept || to_jsonb(t.trip_id);
      continue;
    end if;

    delete from trips where trip_id = t.trip_id;
    v_deleted := v_deleted + 1;
    v_days := v_days || t.date;
  end loop;

  -- Inserts: only into a slot still empty and not skipped.
  for r in select * from jsonb_array_elements(coalesce(p_plan -> 'inserts', '[]'::jsonb)) loop
    if (r ->> 'date')::date <= v_op_day
       or exists (select 1 from roster_skips s
                   where s.date = (r ->> 'date')::date
                     and s.vehicle_id = r ->> 'vehicle_id'
                     and s.shift = r ->> 'shift')
       or exists (select 1 from trips x
                   where x.date = (r ->> 'date')::date
                     and x.vehicle_id = r ->> 'vehicle_id'
                     and x.shift_type = r ->> 'shift') then
      v_dropped := v_dropped + 1;
      continue;
    end if;

    insert into trips (date, shift_type, shift_start_time, shift_end_time, route_id,
                       vehicle_id, driver_id, break_start, roster_month)
    values ((r ->> 'date')::date,
            r ->> 'shift',
            (r ->> 'shift_start_time')::time,
            (r ->> 'shift_end_time')::time,
            (r ->> 'route_id')::integer,
            r ->> 'vehicle_id',
            (r ->> 'driver_id')::integer,
            nullif(r ->> 'break_start', '')::time,
            p_month);

    v_inserted := v_inserted + 1;
    v_days := v_days || (r ->> 'date')::date;
  end loop;

  -- Whatever changed, nobody may be in two places on one shift.
  select date, shift_type, 'driver ' || driver_id as who into v_clash
    from trips
   where date = any (v_days)
   group by date, shift_type, driver_id
  having count(*) > 1
   limit 1;

  if not found then
    select date, shift_type, 'bus ' || vehicle_id as who into v_clash
      from trips
     where date = any (v_days)
     group by date, shift_type, vehicle_id
    having count(*) > 1
     limit 1;
  end if;

  if found then
    raise exception '% would be booked twice on the % shift of %: the schedule changed while publishing',
      v_clash.who, v_clash.shift_type, v_clash.date
      using errcode = 'RS422';
  end if;

  delete from roster_gaps where month = p_month;

  insert into roster_gaps (date, vehicle_id, shift, month, route_id, reason)
  select (g ->> 'date')::date, g ->> 'vehicle_id', g ->> 'shift', p_month,
         nullif(g ->> 'route_id', '')::integer, g ->> 'reason'
    from jsonb_array_elements(coalesce(p_plan -> 'gaps', '[]'::jsonb)) as g
  on conflict (date, vehicle_id, shift) do update set reason = excluded.reason, month = excluded.month;

  -- Every week touched reads as saved, so a planner left open on it refuses its stale save.
  insert into schedule_weeks (week_start, saved_at, saved_by)
  select distinct d - (extract(isodow from d)::integer - 1), v_wall, p_published_by
    from unnest(v_days) as d
  on conflict (week_start) do update set saved_at = excluded.saved_at, saved_by = excluded.saved_by;

  update roster_months
     set status       = 'Published',
         version      = v_version + 1,
         published_at = now(),
         published_by = p_published_by::text
   where month = p_month;

  -- Handed back before returning. The flag is local to the transaction, and a caller that
  -- goes on to change a trip in the same one must meet the triggers as usual.
  perform set_config('routesync.publishing', '', true);

  return jsonb_build_object(
    'version',  v_version + 1,
    'inserted', v_inserted,
    'updated',  v_updated,
    'deleted',  v_deleted,
    'dropped',  v_dropped,
    'kept',     v_kept);
end;
$$;


ALTER FUNCTION "public"."publish_roster_month"("p_month" "date", "p_base_version" integer, "p_plan" "jsonb", "p_published_by" integer) OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."request_leave_withdrawal"("p_request" bigint, "p_reason" "text") RETURNS "void"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
DECLARE
    v_today date := CASE
        WHEN (now() AT TIME ZONE 'Asia/Manila')::time < time '06:00'
            THEN ((now() AT TIME ZONE 'Asia/Manila')::date - 1)
            ELSE (now() AT TIME ZONE 'Asia/Manila')::date
    END;
BEGIN
    UPDATE public.leave_requests
       SET withdraw_requested_at = now(),
           withdraw_reason       = nullif(btrim(p_reason), ''),
           -- A fresh ask, so any previous answer no longer applies.
           withdraw_answered_at  = NULL
     WHERE request_id = p_request
       AND user_id = public.jwt_uid()
       AND status = 'Approved'
       AND start_date >= v_today
       -- Nothing outstanding: never asked, or asked and already answered.
       AND (withdraw_requested_at IS NULL OR withdraw_answered_at IS NOT NULL);

    IF NOT FOUND THEN
        RAISE EXCEPTION 'That leave cannot be withdrawn.';
    END IF;
END;
$$;


ALTER FUNCTION "public"."request_leave_withdrawal"("p_request" bigint, "p_reason" "text") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."save_roster_month"("p_month" "date", "p_base_version" integer, "p_slots" "jsonb", "p_saved_by" integer, "p_held" "jsonb" DEFAULT '[]'::"jsonb") RETURNS integer
    LANGUAGE "plpgsql"
    SET "search_path" TO 'public'
    AS $$
declare
  v_version integer;
begin
  if p_month is null or p_month <> date_trunc('month', p_month)::date then
    raise exception 'roster month must be the first day of a month'
      using errcode = '22023';
  end if;

  insert into roster_months (month) values (p_month) on conflict (month) do nothing;

  select version into v_version from roster_months where month = p_month for update;

  if v_version <> p_base_version then
    raise exception 'roster for % was saved by someone else (version % not %)',
      to_char(p_month, 'YYYY-MM'), v_version, p_base_version
      using errcode = 'RS409';
  end if;

  update roster_months
     set version  = v_version + 1,
         saved_at = now(),
         saved_by = p_saved_by
   where month = p_month;

  delete from roster_slots where month = p_month;

  insert into roster_slots (month, driver_id, kind, route_id, vehicle_id, shift, rest_weekday, suggested)
  select p_month,
         (s ->> 'driver_id')::integer,
         s ->> 'kind',
         (s ->> 'route_id')::integer,
         nullif(s ->> 'vehicle_id', ''),
         s ->> 'shift',
         (s ->> 'rest_weekday')::smallint,
         nullif(s ->> 'suggested', '')
    from jsonb_array_elements(coalesce(p_slots, '[]'::jsonb)) as s;

  delete from roster_holds where month = p_month;

  -- A driver the roster places is not held back from it.
  insert into roster_holds (month, driver_id)
  select distinct p_month, h::integer
    from jsonb_array_elements_text(coalesce(p_held, '[]'::jsonb)) as h
   where not exists (
           select 1 from roster_slots s
            where s.month = p_month and s.driver_id = h::integer);

  return v_version + 1;
end;
$$;


ALTER FUNCTION "public"."save_roster_month"("p_month" "date", "p_base_version" integer, "p_slots" "jsonb", "p_saved_by" integer, "p_held" "jsonb") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."schedule_weeks_roster_only"() RETURNS "trigger"
    LANGUAGE "plpgsql"
    SET "search_path" TO 'public'
    AS $$
begin
  if coalesce(current_setting('routesync.publishing', true), '') <> 'on' then
    new.roster_only := false;
  elsif tg_op = 'INSERT' then
    new.roster_only := true;
  else
    new.roster_only := old.roster_only;
  end if;
  return new;
end;
$$;


ALTER FUNCTION "public"."schedule_weeks_roster_only"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."shift_start_context"("p_trip_id" character varying) RETURNS TABLE("open_trip_id" character varying, "open_trip_ends_at" timestamp without time zone, "bus_inspected_today" boolean)
    LANGUAGE "plpgsql" STABLE SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
  v_uid       integer := public.jwt_uid();
  v_role      text    := coalesce(nullif(current_setting('request.jwt.claims', true), '')::json ->> 'role', 'db');
  v_vehicle   character varying(20);
  v_now_ph    timestamp   := now() at time zone 'Asia/Manila';
  v_day_start timestamp;
  v_day_wall  timestamptz;
  v_open_id   character varying(20);
  v_open_ends timestamp;
  v_inspected boolean;
begin
  -- The caller's own shift, or nothing. A key with no user id behind it, and a trip
  -- belonging to somebody else, are the same answer on purpose.
  select t.vehicle_id into v_vehicle
    from public.trips t
   where t.trip_id = p_trip_id
     and (v_role not in ('app_driver', 'app_camera') or t.driver_id = v_uid);

  if v_vehicle is null then
    return;
  end if;

  -- The operational day runs from six in the morning, so a night shift belongs to the day
  -- it started and the next morning's driver inspects again. The same rule the apps apply.
  v_day_start := case
                   when v_now_ph::time < time '06:00'
                     then (v_now_ph::date - 1) + time '06:00'
                   else v_now_ph::date + time '06:00'
                 end;
  v_day_wall  := v_day_start at time zone 'UTC';

  -- The open trip that is due to end first. A bus carrying more than one is already
  -- inconsistent, and the earliest is the one a handover is answering. Both values stay
  -- null when the bus is free, which is the ordinary case.
  select o.trip_id, o.ends_at
    into v_open_id, v_open_ends
    from (
      select t.trip_id,
             t.date + t.shift_end_time
               + case when t.shift_end_time <= t.shift_start_time then interval '1 day'
                      else interval '0' end as ends_at
        from public.trips t
       where t.vehicle_id = v_vehicle
         and t.trip_id <> p_trip_id
         and t.trip_status = 'Active'
       order by ends_at
       limit 1
    ) o;

  -- Cleared means inspected and drivable. A failed inspection is not an inspection for
  -- this purpose: it grounds the bus, and a later driver must not be offered a skip on
  -- the strength of it.
  select exists (
    select 1
      from public.bus_checklist c
      join public.trips ct on ct.trip_id = c.trip_id
     where ct.vehicle_id = v_vehicle
       and c.submitted_at >= v_day_wall
       and c.checklist_status::text in ('Passed', 'Passed with Defects', 'Skipped')
  ) into v_inspected;

  open_trip_id        := v_open_id;
  open_trip_ends_at   := v_open_ends;
  bus_inspected_today := v_inspected;
  return next;
end
$$;


ALTER FUNCTION "public"."shift_start_context"("p_trip_id" character varying) OWNER TO "postgres";


COMMENT ON FUNCTION "public"."shift_start_context"("p_trip_id" character varying) IS 'The open trip on this shift bus, when it is due to end, and whether the bus has been inspected in this operational day. Answers only for a trip the caller is driving, and never names another driver.';



CREATE OR REPLACE FUNCTION "public"."trips_closed_guard"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
  v_claims json;
  v_role   text;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');

  if v_role <> 'app_driver' then
    return NEW;
  end if;

  -- Only the driver's End is worth a row. It is the write that carries a completed status
  -- and an end time of its own.
  if NEW.trip_status = 'Completed'
     and NEW.actual_end_time is distinct from OLD.actual_end_time then
    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      ('user', v_claims->>'user_id', v_role, 'trip_end_after_close', 'trips', OLD.trip_id,
       'db', 'ok',
       format('Trip %s: driver %s ended it on their phone after it had already closed. The trip keeps its end time and a count of %s; the phone sent a count of %s.',
              OLD.trip_id, OLD.driver_id, OLD.total_boarded, NEW.total_boarded),
       jsonb_build_object(
         'kept_end_time',   OLD.actual_end_time,
         'sent_end_time',   NEW.actual_end_time,
         'kept_total',      OLD.total_boarded,
         'sent_total',      NEW.total_boarded,
         'vehicle_id',      OLD.vehicle_id));
  end if;

  return null;
end
$$;


ALTER FUNCTION "public"."trips_closed_guard"() OWNER TO "postgres";


COMMENT ON FUNCTION "public"."trips_closed_guard"() IS 'Drops any write from the driver app to a trip that is already Completed, and files a late End as trip_end_after_close.';



CREATE OR REPLACE FUNCTION "public"."trips_count_guard"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
  v_claims      json;
  v_role        text;
  v_actor_type  text;
  v_actor_id    text;
  v_device      boolean;
  v_claim       integer;
  v_final       integer;
  v_fare        numeric(10,2);
  v_camera_live boolean;
  v_may_lower   boolean := false;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');
  v_device := v_role in ('app_camera', 'app_driver');

  v_claim := coalesce(NEW.total_boarded, OLD.total_boarded);

  if not v_device then
    -- The service key sets the figure literally, which is how an admin settles a count.
    v_final := v_claim;
  else
    -- Twelve seconds, matching HeartbeatStale in the driver app. The counter phone
    -- stamps every five, so a live camera is never mistaken for a dark one.
    v_camera_live := OLD.count_heartbeat is not null
                     and OLD.count_heartbeat > now() - interval '12 seconds';

    -- Only the driver, and only with no camera counting. The counter phone can never
    -- lower a figure: its own count is monotonic, so a lower one from it is stale.
    v_may_lower := v_role = 'app_driver' and not v_camera_live;

    v_final := case when v_may_lower then v_claim
                    else greatest(OLD.total_boarded, v_claim) end;
  end if;

  NEW.total_boarded := greatest(0, v_final);

  -- Revenue follows the count. A phone never authors this figure: the driver app sends
  -- one computed from whatever total it held, and the counter phone cannot write the
  -- column at all. A write through the service key naming its own revenue keeps it.
  if v_device
     or (NEW.total_boarded is distinct from OLD.total_boarded
         and NEW.estimated_revenue is not distinct from OLD.estimated_revenue) then
    select standard_fare into v_fare from public.fare_config where id = 1;
    NEW.estimated_revenue := round(NEW.total_boarded * coalesce(v_fare, 0), 2);
  end if;

  -- ------------------------------------------------------------------------
  -- Trail. Only the exceptional transitions, so a count climbing one passenger at a
  -- time does not bury the security events audit_log exists for.
  -- ------------------------------------------------------------------------

  v_actor_type := case v_role
    when 'app_driver'   then 'user'
    when 'app_camera'   then 'device'
    when 'service_role' then 'admin'
    when 'anon'         then 'anon'
    else 'system'
  end;
  v_actor_id := coalesce(v_claims->>'user_id', v_claims->>'device_id');

  -- A surface that has been out of contact sending a figure from before the divergence.
  if v_device and not v_may_lower
     and v_claim is distinct from OLD.total_boarded
     and v_claim < OLD.total_boarded then
    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      (v_actor_type, v_actor_id, v_role, 'count_clamped', 'trips', NEW.trip_id,
       'db', 'ok',
       format('Trip %s: a count of %s was not applied because %s had already been counted.',
              NEW.trip_id, v_claim, OLD.total_boarded),
       jsonb_build_object('claimed', v_claim, 'total_boarded', NEW.total_boarded));
  end if;

  -- The driver taking the count down while no camera was counting. Permitted, and the
  -- only way a device can reduce a recorded figure, so it is written down.
  if v_may_lower and v_claim < OLD.total_boarded then
    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      (v_actor_type, v_actor_id, v_role, 'count_lowered', 'trips', NEW.trip_id,
       'db', 'ok',
       format('Trip %s: driver corrected the count from %s to %s while no camera was counting.',
              NEW.trip_id, OLD.total_boarded, v_claim),
       jsonb_build_object('was', OLD.total_boarded, 'total_boarded', NEW.total_boarded));
  end if;

  -- A count arriving after the trip closed, which is a counter phone reconciling from a
  -- dead zone.
  if v_device and OLD.trip_status = 'Completed'
     and NEW.total_boarded > OLD.total_boarded then
    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      (v_actor_type, v_actor_id, v_role, 'count_late_raise', 'trips', NEW.trip_id,
       'db', 'ok',
       format('Trip %s: count raised to %s after the trip was completed, previously %s.',
              NEW.trip_id, NEW.total_boarded, OLD.total_boarded),
       jsonb_build_object('claimed', v_claim, 'total_boarded', NEW.total_boarded));
  end if;

  return NEW;
end
$$;


ALTER FUNCTION "public"."trips_count_guard"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."trips_roster_guard"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
begin
  if coalesce(current_setting('routesync.publishing', true), '') = 'on' then
    if tg_op = 'DELETE' then return old; end if;
    return new;
  end if;

  if tg_op = 'UPDATE' then
    new.hand_edited := true;
    return new;
  end if;

  if tg_op = 'DELETE' then
    insert into roster_skips (date, vehicle_id, shift, month, reason)
    values (old.date, old.vehicle_id, old.shift_type, old.roster_month, 'Deleted')
    on conflict (date, vehicle_id, shift) do nothing;
    return old;
  end if;

  return null;
end;
$$;


ALTER FUNCTION "public"."trips_roster_guard"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."trips_takeover_guard"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
  v_claims     json;
  v_role       text;
  v_actor_type text;
  v_actor_id   text;
  v_device     boolean;
  v_now_ph     timestamp;    -- the Philippine wall clock, as a plain local value
  v_wall       timestamptz;  -- the same reading, stored the way a phone stores it
  v_end        timestamp;    -- the standing trip's scheduled end, on the same clock
  v_past       boolean;
  v_summary    text;
  v_open       record;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');
  v_device := v_role in ('app_camera', 'app_driver');

  v_now_ph := now() at time zone 'Asia/Manila';
  v_wall   := v_now_ph at time zone 'UTC';

  v_actor_type := case v_role
    when 'app_driver'   then 'user'
    when 'app_camera'   then 'device'
    when 'service_role' then 'admin'
    when 'anon'         then 'anon'
    else 'system'
  end;
  v_actor_id := coalesce(v_claims->>'user_id', v_claims->>'device_id');

  -- Every other trip the bus is already on, oldest first, locked for the length of this
  -- statement. Two drivers pressing Start at the same moment queue here: the second one
  -- re-reads the row the first just closed, finds it no longer Active, and starts cleanly
  -- instead of closing it a second time.
  --
  -- A loop rather than a single lookup, because data written before this rule existed can
  -- already have a bus on more than one active trip, and settling only one of them would
  -- leave the bus in the state this exists to prevent.
  for v_open in
    select trip_id, driver_id, date, shift_start_time, shift_end_time
      from public.trips
     where vehicle_id = NEW.vehicle_id
       and trip_id <> NEW.trip_id
       and trip_status = 'Active'
     order by date, shift_start_time, trip_id
       for update
  loop
    -- The same rule as ScheduledEnd in the stale trip closer. An end at or before the
    -- start means the shift runs overnight, so it lands on the following day.
    v_end := v_open.date + v_open.shift_end_time
             + case when v_open.shift_end_time <= v_open.shift_start_time
                      then interval '1 day'
                      else interval '0 days'
               end;
    v_past := v_now_ph > v_end;

    -- Still driving. Only a privileged key may end somebody else's shift for them.
    if not v_past and v_device then
      raise exception
        'Bus % is still on trip %, which is due to end at %. Start again once that shift is over, or ask dispatch to close it.',
        NEW.vehicle_id, v_open.trip_id, to_char(v_end, 'DD Mon HH24:MI')
        using errcode = 'RS409';
    end if;

    update public.trips
       set trip_status     = 'Completed',
           actual_end_time = v_wall
     where trip_id = v_open.trip_id;

    if v_past then
      v_summary := format(
        'Trip %s on bus %s was closed at handover: driver %s left it active past its scheduled end of %s, and driver %s started trip %s on the same bus.',
        v_open.trip_id, NEW.vehicle_id, v_open.driver_id,
        to_char(v_end, 'DD Mon HH24:MI'), NEW.driver_id, NEW.trip_id);
    else
      v_summary := format(
        'Trip %s on bus %s was closed at handover before its scheduled end of %s: driver %s was still on it, and driver %s started trip %s on the same bus.',
        v_open.trip_id, NEW.vehicle_id, to_char(v_end, 'DD Mon HH24:MI'),
        v_open.driver_id, NEW.driver_id, NEW.trip_id);
    end if;

    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      (v_actor_type, v_actor_id, v_role, 'trip_taken_over', 'trips', v_open.trip_id,
       'db', 'ok', v_summary,
       jsonb_build_object(
         'closed_trip_id',     v_open.trip_id,
         'new_trip_id',        NEW.trip_id,
         'closed_driver_id',   v_open.driver_id,
         'new_driver_id',      NEW.driver_id,
         'vehicle_id',         NEW.vehicle_id,
         'scheduled_end',      to_char(v_end, 'YYYY-MM-DD HH24:MI'),
         'past_scheduled_end', v_past));
  end loop;

  return NEW;
end
$$;


ALTER FUNCTION "public"."trips_takeover_guard"() OWNER TO "postgres";


COMMENT ON FUNCTION "public"."trips_takeover_guard"() IS 'Holds a bus to one active trip. Closes a standing trip that is past its scheduled end when the next one starts, refuses the start while the standing trip is still inside its hours, and files the close in audit_log as trip_taken_over.';



CREATE OR REPLACE FUNCTION "public"."users_email_lower"() RETURNS "trigger"
    LANGUAGE "plpgsql"
    SET "search_path" TO 'public'
    AS $$
begin
  new.email_address := lower(btrim(new.email_address));
  return new;
end;
$$;


ALTER FUNCTION "public"."users_email_lower"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."vehicles_release_guard"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
  v_claims json;
  v_uid    integer;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;

  if coalesce(v_claims->>'role', 'db') <> 'app_driver' then
    return NEW;
  end if;

  v_uid := nullif(v_claims->>'user_id', '')::integer;

  if exists (select 1
               from public.trips t
              where t.vehicle_id = NEW.vehicle_id
                and t.trip_status = 'Active'
                and t.driver_id is distinct from v_uid) then
    return null;
  end if;

  return NEW;
end
$$;


ALTER FUNCTION "public"."vehicles_release_guard"() OWNER TO "postgres";


COMMENT ON FUNCTION "public"."vehicles_release_guard"() IS 'Drops a driver app change to the status of a bus that another driver has an active trip on.';


SET default_tablespace = '';

SET default_table_access_method = "heap";


CREATE TABLE IF NOT EXISTS "public"."audit_log" (
    "id" bigint NOT NULL,
    "occurred_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "actor_type" "text" NOT NULL,
    "actor_id" "text",
    "actor_role" "text",
    "action" "text" NOT NULL,
    "target_table" "text",
    "target_id" "text",
    "source" "text" NOT NULL,
    "outcome" "text" DEFAULT 'ok'::"text" NOT NULL,
    "summary" "text",
    "changes" "jsonb",
    "ip" "text",
    "request_id" "text"
);


ALTER TABLE "public"."audit_log" OWNER TO "postgres";


ALTER TABLE "public"."audit_log" ALTER COLUMN "id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."audit_log_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."boarding_events" (
    "event_id" "text" NOT NULL,
    "trip_id" character varying(20) NOT NULL,
    "counter_device_id" "text" NOT NULL,
    "direction" "text" NOT NULL,
    "device_timestamp" timestamp with time zone NOT NULL,
    "synced_at" timestamp with time zone,
    "received_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    CONSTRAINT "ck_boarding_events_direction" CHECK (("direction" = ANY (ARRAY['in'::"text", 'out'::"text"])))
);


ALTER TABLE "public"."boarding_events" OWNER TO "postgres";


COMMENT ON TABLE "public"."boarding_events" IS 'One detected doorway crossing. Evidence for the count in trips.total_boarded, never the count itself.';



COMMENT ON COLUMN "public"."boarding_events"."event_id" IS 'Generated on the device at the crossing, so a resend collides here and stores once.';



COMMENT ON COLUMN "public"."boarding_events"."counter_device_id" IS 'The phone that detected it, as it was then. Deliberately not a foreign key: no table of devices exists, and the vehicle binding it would point at changes when a phone is swapped.';



COMMENT ON COLUMN "public"."boarding_events"."direction" IS 'in for a boarding, out for a crossing the counter detected and excluded. Only in is ever counted.';



COMMENT ON COLUMN "public"."boarding_events"."device_timestamp" IS 'When the crossing happened, by the device clock.';



COMMENT ON COLUMN "public"."boarding_events"."synced_at" IS 'When the event left the device queue. Null when it was sent without being held. With device_timestamp, both on the same clock, it gives the time spent waiting for a signal.';



COMMENT ON COLUMN "public"."boarding_events"."received_at" IS 'When the database accepted it. With device_timestamp it gives end to end latency.';



CREATE OR REPLACE VIEW "public"."boarding_events_ph" AS
 SELECT "event_id",
    "trip_id",
    "counter_device_id",
    "direction",
    ("device_timestamp" AT TIME ZONE 'Asia/Manila'::"text") AS "device_time_ph",
    ("synced_at" AT TIME ZONE 'Asia/Manila'::"text") AS "synced_ph",
    ("received_at" AT TIME ZONE 'Asia/Manila'::"text") AS "received_ph",
    EXTRACT(epoch FROM ("received_at" - "device_timestamp")) AS "latency_seconds",
    EXTRACT(epoch FROM ("received_at" - COALESCE("synced_at", "device_timestamp"))) AS "transit_seconds"
   FROM "public"."boarding_events";


ALTER VIEW "public"."boarding_events_ph" OWNER TO "postgres";


COMMENT ON VIEW "public"."boarding_events_ph" IS 'boarding_events in Philippine wall-clock time, with latency worked out on the stored instants so a device clock offset cannot distort it.';



CREATE TABLE IF NOT EXISTS "public"."bus_checklist" (
    "checklist_id" integer NOT NULL,
    "trip_id" character varying(20) NOT NULL,
    "vehicle_id" character varying(20) NOT NULL,
    "driver_id" integer NOT NULL,
    "submitted_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "exterior_inspection" "jsonb" NOT NULL,
    "engine_compartment" "jsonb" NOT NULL,
    "interior_inspection" "jsonb" NOT NULL,
    "brake_safety" "jsonb" NOT NULL,
    "passenger_systems" "jsonb" NOT NULL,
    "checklist_status" "public"."checklist_status_enum" DEFAULT 'Pending'::"public"."checklist_status_enum" NOT NULL,
    "notes" "text"
);


ALTER TABLE "public"."bus_checklist" OWNER TO "postgres";


COMMENT ON COLUMN "public"."bus_checklist"."checklist_status" IS 'The inspection outcome for this trip. Skipped records that no inspection took place, which is what lets a day with no inspection be told apart from a day with no record. It is never a pass and it never clears vehicles.out_of_service.';



ALTER TABLE "public"."bus_checklist" ALTER COLUMN "checklist_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."bus_checklist_checklist_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."checklist_items" (
    "item_id" integer NOT NULL,
    "section_key" "text" NOT NULL,
    "section_title" "text" NOT NULL,
    "label" "text" NOT NULL,
    "is_critical" boolean DEFAULT false NOT NULL,
    "sort_order" integer NOT NULL,
    "active" boolean DEFAULT true NOT NULL
);


ALTER TABLE "public"."checklist_items" OWNER TO "postgres";


ALTER TABLE "public"."checklist_items" ALTER COLUMN "item_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."checklist_items_item_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."device_config" (
    "device_id" "text" NOT NULL,
    "line_ax" real,
    "line_ay" real,
    "line_bx" real,
    "line_by" real,
    "inward_sign" integer DEFAULT 1 NOT NULL,
    "use_back_camera" boolean DEFAULT false NOT NULL,
    "wake_requested_at" timestamp with time zone,
    "version" integer DEFAULT 0 NOT NULL,
    "updated_by" "text",
    "updated_at" timestamp with time zone DEFAULT "now"()
);


ALTER TABLE "public"."device_config" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."device_health_log" (
    "log_id" bigint NOT NULL,
    "device_id" "text" NOT NULL,
    "trip_id" character varying(20),
    "event_type" "text" NOT NULL,
    "battery_level" integer,
    "occurred_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "is_charging" boolean,
    CONSTRAINT "ck_device_health_log_battery_level" CHECK ((("battery_level" IS NULL) OR (("battery_level" >= 0) AND ("battery_level" <= 100)))),
    CONSTRAINT "ck_device_health_log_event_type" CHECK (("event_type" = ANY (ARRAY['restart'::"text", 'battery_reading'::"text"])))
);


ALTER TABLE "public"."device_health_log" OWNER TO "postgres";


COMMENT ON TABLE "public"."device_health_log" IS 'What the counter phone reports about its own running. Restarts and charge levels only, because a crashed or frozen application cannot report itself.';



COMMENT ON COLUMN "public"."device_health_log"."device_id" IS 'The phone that reported it, as it identified itself then. Deliberately not a foreign key: no table of devices exists, and the vehicle binding it would point at changes when a phone is swapped.';



COMMENT ON COLUMN "public"."device_health_log"."trip_id" IS 'The run this happened during, when there was one. Null for a restart or a reading outside a trip, which is still worth recording.';



COMMENT ON COLUMN "public"."device_health_log"."event_type" IS 'restart when the application started, battery_reading for a charge level. A crash is read as a restart with no clean shutdown behind it, and a freeze as a gap in device_status.last_seen.';



COMMENT ON COLUMN "public"."device_health_log"."battery_level" IS 'Charge percentage at the moment of the reading. Null on a restart row, which reports an event rather than a level.';



COMMENT ON COLUMN "public"."device_health_log"."occurred_at" IS 'When the event happened. Stored as a real instant, read in Philippine time through device_health_log_ph.';



COMMENT ON COLUMN "public"."device_health_log"."is_charging" IS 'Whether the phone was plugged in when the level was read. Null on a restart row.';



ALTER TABLE "public"."device_health_log" ALTER COLUMN "log_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."device_health_log_log_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE OR REPLACE VIEW "public"."device_health_log_ph" AS
 SELECT "log_id",
    "device_id",
    "trip_id",
    "event_type",
    "battery_level",
    ("occurred_at" AT TIME ZONE 'Asia/Manila'::"text") AS "occurred_ph"
   FROM "public"."device_health_log";


ALTER VIEW "public"."device_health_log_ph" OWNER TO "postgres";


COMMENT ON VIEW "public"."device_health_log_ph" IS 'device_health_log in Philippine wall-clock time, so a restart can be placed against the shift it interrupted.';



CREATE TABLE IF NOT EXISTS "public"."device_status" (
    "device_id" "text" NOT NULL,
    "last_seen" timestamp with time zone,
    "wake_state" "text" DEFAULT 'idle'::"text" NOT NULL,
    "snapshot_ready_at" timestamp with time zone,
    "applied_at" timestamp with time zone,
    "config_version_applied" integer DEFAULT '-1'::integer NOT NULL,
    "unreconciled_counts" integer DEFAULT 0 NOT NULL,
    "unreconciled_oldest_at" timestamp with time zone,
    "battery_level" integer,
    "battery_charging" boolean,
    "battery_read_at" timestamp with time zone,
    CONSTRAINT "ck_device_status_battery_level" CHECK ((("battery_level" IS NULL) OR (("battery_level" >= 0) AND ("battery_level" <= 100))))
);


ALTER TABLE "public"."device_status" OWNER TO "postgres";


COMMENT ON COLUMN "public"."device_status"."unreconciled_counts" IS 'Trips this device has counted but cannot confirm as stored. Zero in normal operation. A number that does not fall means deliveries are failing.';



COMMENT ON COLUMN "public"."device_status"."unreconciled_oldest_at" IS 'When the oldest undelivered count was made, so a backlog can be told apart from a count that is merely a few seconds behind.';



COMMENT ON COLUMN "public"."device_status"."battery_level" IS 'Charge percentage at the last reading. Null until the phone has reported one.';



COMMENT ON COLUMN "public"."device_status"."battery_charging" IS 'Whether the phone was plugged in at the last reading.';



COMMENT ON COLUMN "public"."device_status"."battery_read_at" IS 'When the phone last reported its charge. It reports on a change rather than on a schedule, so an old time with a live heartbeat means the level has held steady.';



CREATE TABLE IF NOT EXISTS "public"."driver_availability" (
    "user_id" integer NOT NULL,
    "availability_status" character varying(20) DEFAULT 'Available'::character varying NOT NULL,
    "updated_at" timestamp without time zone,
    "reason" "text",
    CONSTRAINT "driver_availability_availability_status_check" CHECK ((("availability_status")::"text" = ANY ((ARRAY['Available'::character varying, 'Unavailable'::character varying])::"text"[])))
);


ALTER TABLE "public"."driver_availability" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."fare_config" (
    "id" integer DEFAULT 1 NOT NULL,
    "standard_fare" numeric(10,2) NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"()
);


ALTER TABLE "public"."fare_config" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."inspection_photos" (
    "photo_id" bigint NOT NULL,
    "checklist_id" integer NOT NULL,
    "checklist_item_id" bigint NOT NULL,
    "log_id" integer,
    "object_key" "text",
    "taken_at" timestamp with time zone NOT NULL,
    "uploaded_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "swept_at" timestamp with time zone,
    CONSTRAINT "ck_inspection_photos_swept" CHECK ((("object_key" IS NULL) = ("swept_at" IS NOT NULL)))
);


ALTER TABLE "public"."inspection_photos" OWNER TO "postgres";


COMMENT ON TABLE "public"."inspection_photos" IS 'A photograph of a failed inspection item. Evidence for a fault, never the decision about one.';



COMMENT ON COLUMN "public"."inspection_photos"."checklist_id" IS 'The inspection this was taken during. Cascades, because a photograph of an inspection that no longer exists describes nothing.';



COMMENT ON COLUMN "public"."inspection_photos"."checklist_item_id" IS 'The item that failed. What a work order line matches on to gather its photographs.';



COMMENT ON COLUMN "public"."inspection_photos"."log_id" IS 'The order this fault was raised onto, carried so retention and reading need no walk through the vehicle. Nulled rather than cascaded if the order goes, since the photograph is still evidence of the inspection.';



COMMENT ON COLUMN "public"."inspection_photos"."object_key" IS 'Path in the inspection-photos bucket, named by the device. Null once the photograph has been swept, which the check constraint ties to swept_at.';



COMMENT ON COLUMN "public"."inspection_photos"."taken_at" IS 'When the photograph was taken, by the device clock.';



COMMENT ON COLUMN "public"."inspection_photos"."uploaded_at" IS 'When the row was recorded, after the object was confirmed present.';



COMMENT ON COLUMN "public"."inspection_photos"."swept_at" IS 'When the object was deleted under the retention rule. The row survives so that a photograph that aged out stays distinguishable from one that was never taken.';



ALTER TABLE "public"."inspection_photos" ALTER COLUMN "photo_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."inspection_photos_photo_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."leave_requests" (
    "request_id" bigint NOT NULL,
    "user_id" integer NOT NULL,
    "leave_type" character varying(20) NOT NULL,
    "start_date" "date" NOT NULL,
    "end_date" "date" NOT NULL,
    "reason" "text",
    "status" character varying(20) DEFAULT 'Pending'::character varying NOT NULL,
    "filed_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "decided_by" integer,
    "decided_at" timestamp with time zone,
    "decision_note" "text",
    "revoked_dates" "date"[],
    "revoked_at" timestamp with time zone,
    "revoked_by" integer,
    "revoke_note" "text",
    "withdraw_requested_at" timestamp with time zone,
    "withdraw_reason" "text",
    "withdraw_answered_at" timestamp with time zone,
    "withdraw_answered_by" integer,
    "withdraw_answer_note" "text",
    CONSTRAINT "leave_requests_leave_type_check" CHECK ((("leave_type")::"text" = ANY (ARRAY[('Vacation'::character varying)::"text", ('Sick'::character varying)::"text", ('Emergency'::character varying)::"text"]))),
    CONSTRAINT "leave_requests_range_check" CHECK (("end_date" >= "start_date")),
    CONSTRAINT "leave_requests_status_check" CHECK ((("status")::"text" = ANY ((ARRAY['Pending'::character varying, 'AwaitingChange'::character varying, 'Approved'::character varying, 'Rejected'::character varying, 'Cancelled'::character varying, 'Revoked'::character varying])::"text"[])))
);


ALTER TABLE "public"."leave_requests" OWNER TO "postgres";


ALTER TABLE "public"."leave_requests" ALTER COLUMN "request_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."leave_requests_request_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."maintenance_items" (
    "item_id" bigint NOT NULL,
    "log_id" integer NOT NULL,
    "label" "text" NOT NULL,
    "is_critical" boolean DEFAULT false NOT NULL,
    "source" "text" DEFAULT 'manual'::"text" NOT NULL,
    "state" "text" DEFAULT 'open'::"text" NOT NULL,
    "closed_at" timestamp with time zone,
    "closed_by" "text",
    "note" "text",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "checklist_item_id" bigint,
    CONSTRAINT "maintenance_items_critical_not_dismissed" CHECK ((NOT ("is_critical" AND ("state" = 'dismissed'::"text")))),
    CONSTRAINT "maintenance_items_source_check" CHECK (("source" = ANY (ARRAY['checklist'::"text", 'manual'::"text"]))),
    CONSTRAINT "maintenance_items_state_check" CHECK (("state" = ANY (ARRAY['open'::"text", 'fixed'::"text", 'dismissed'::"text"])))
);


ALTER TABLE "public"."maintenance_items" OWNER TO "postgres";


COMMENT ON TABLE "public"."maintenance_items" IS 'The faults being worked under one maintenance_logs order, one row per fault.';



COMMENT ON COLUMN "public"."maintenance_items"."is_critical" IS 'Whether failing this grounds the bus. Set from checklist_items; hand-typed items are never critical.';



COMMENT ON COLUMN "public"."maintenance_items"."state" IS 'open until closed as fixed, or dismissed when the fault was not real.';



COMMENT ON COLUMN "public"."maintenance_items"."checklist_item_id" IS 'The inspection item this line was raised by, when one was. Null for a line typed by hand and for every line raised before it was recorded, both of which are matched by label instead.';



ALTER TABLE "public"."maintenance_items" ALTER COLUMN "item_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."maintenance_items_item_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."maintenance_logs" (
    "log_id" integer NOT NULL,
    "checklist_id" integer,
    "vehicle_id" character varying(20) NOT NULL,
    "trip_id" character varying(20),
    "issue_details" "jsonb" NOT NULL,
    "maintenance_status" "public"."maintenance_status_enum" DEFAULT 'Needs Attention'::"public"."maintenance_status_enum" NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "resolved_at" timestamp with time zone,
    "remarks" "text",
    "verified_by" "text"
);


ALTER TABLE "public"."maintenance_logs" OWNER TO "postgres";


ALTER TABLE "public"."maintenance_logs" ALTER COLUMN "log_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."maintenance_logs_log_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."maintenance_notes" (
    "note_id" bigint NOT NULL,
    "log_id" integer NOT NULL,
    "author_id" integer,
    "author_name" "text",
    "action" "text",
    "note" "text",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL
);


ALTER TABLE "public"."maintenance_notes" OWNER TO "postgres";


ALTER TABLE "public"."maintenance_notes" ALTER COLUMN "note_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."maintenance_notes_note_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."messages" (
    "message_id" integer NOT NULL,
    "sender_id" integer NOT NULL,
    "target_audience" "public"."target_audience_enum" NOT NULL,
    "target_id" character varying(20),
    "subject" character varying(255),
    "body" "text" NOT NULL,
    "priority" "public"."priority_enum" DEFAULT 'Normal'::"public"."priority_enum" NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "is_read" boolean DEFAULT false NOT NULL
);


ALTER TABLE "public"."messages" OWNER TO "postgres";


ALTER TABLE "public"."messages" ALTER COLUMN "message_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."messages_message_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."password_reset_otp" (
    "id" bigint NOT NULL,
    "user_id" integer NOT NULL,
    "otp_hash" "text" NOT NULL,
    "expires_at" timestamp with time zone NOT NULL,
    "attempts" integer DEFAULT 0 NOT NULL,
    "consumed_at" timestamp with time zone,
    "completed_at" timestamp with time zone,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "ip" "text"
);


ALTER TABLE "public"."password_reset_otp" OWNER TO "postgres";


ALTER TABLE "public"."password_reset_otp" ALTER COLUMN "id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."password_reset_otp_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."roles" (
    "role_id" integer NOT NULL,
    "role_name" character varying(50) NOT NULL,
    "access_level" character varying(50) NOT NULL,
    "web_permissions" "jsonb" NOT NULL,
    "mobile_permissions" "jsonb" NOT NULL
);


ALTER TABLE "public"."roles" OWNER TO "postgres";


ALTER TABLE "public"."roles" ALTER COLUMN "role_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."roles_role_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."roster_gaps" (
    "date" "date" NOT NULL,
    "vehicle_id" character varying(20) NOT NULL,
    "shift" character varying(20) NOT NULL,
    "month" "date" NOT NULL,
    "route_id" integer,
    "reason" "text" NOT NULL,
    CONSTRAINT "roster_gaps_shift" CHECK ((("shift")::"text" = ANY ((ARRAY['Morning'::character varying, 'Afternoon'::character varying, 'Evening'::character varying])::"text"[])))
);


ALTER TABLE "public"."roster_gaps" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."roster_holds" (
    "month" "date" NOT NULL,
    "driver_id" integer NOT NULL
);


ALTER TABLE "public"."roster_holds" OWNER TO "postgres";


COMMENT ON TABLE "public"."roster_holds" IS 'Drivers held back from a month''s roster automation. Auto-fill and the suggestions pass them over; one-off cover and placing by hand do not.';



CREATE TABLE IF NOT EXISTS "public"."roster_months" (
    "month" "date" NOT NULL,
    "status" "text" DEFAULT 'Draft'::"text" NOT NULL,
    "version" integer DEFAULT 0 NOT NULL,
    "saved_at" timestamp with time zone,
    "saved_by" integer,
    "generated_at" timestamp with time zone,
    "generated_by" "text",
    "published_at" timestamp with time zone,
    "published_by" "text",
    CONSTRAINT "roster_months_first_of_month" CHECK (("month" = ("date_trunc"('month'::"text", ("month")::timestamp with time zone))::"date")),
    CONSTRAINT "roster_months_status" CHECK (("status" = ANY (ARRAY['Draft'::"text", 'Published'::"text"])))
);


ALTER TABLE "public"."roster_months" OWNER TO "postgres";


COMMENT ON TABLE "public"."roster_months" IS 'One roster per month. version is bumped by every save_roster_month call and a save built on an older version is refused.';



CREATE TABLE IF NOT EXISTS "public"."roster_skips" (
    "date" "date" NOT NULL,
    "vehicle_id" character varying(20) NOT NULL,
    "shift" character varying(20) NOT NULL,
    "month" "date" NOT NULL,
    "reason" "text" NOT NULL,
    "created_by" "text",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    CONSTRAINT "roster_skips_reason" CHECK (("reason" = ANY (ARRAY['Deleted'::"text", 'Not running'::"text"]))),
    CONSTRAINT "roster_skips_shift" CHECK ((("shift")::"text" = ANY ((ARRAY['Morning'::character varying, 'Afternoon'::character varying, 'Evening'::character varying])::"text"[])))
);


ALTER TABLE "public"."roster_skips" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."roster_slots" (
    "month" "date" NOT NULL,
    "driver_id" integer,
    "kind" "text" NOT NULL,
    "route_id" integer NOT NULL,
    "vehicle_id" character varying(20),
    "shift" character varying(20) NOT NULL,
    "rest_weekday" smallint,
    "slot_id" bigint NOT NULL,
    "suggested" "text",
    CONSTRAINT "roster_slots_crew_has_bus" CHECK ((("kind" = 'Crew'::"text") = ("vehicle_id" IS NOT NULL))),
    CONSTRAINT "roster_slots_driver_rests" CHECK ((("driver_id" IS NULL) = ("rest_weekday" IS NULL))),
    CONSTRAINT "roster_slots_floater_has_driver" CHECK ((("kind" = 'Crew'::"text") OR ("driver_id" IS NOT NULL))),
    CONSTRAINT "roster_slots_kind" CHECK (("kind" = ANY (ARRAY['Crew'::"text", 'Floater'::"text"]))),
    CONSTRAINT "roster_slots_rest_weekday" CHECK ((("rest_weekday" >= 1) AND ("rest_weekday" <= 7))),
    CONSTRAINT "roster_slots_shift" CHECK ((("shift")::"text" = ANY ((ARRAY['Morning'::character varying, 'Afternoon'::character varying, 'Evening'::character varying])::"text"[])))
);


ALTER TABLE "public"."roster_slots" OWNER TO "postgres";


COMMENT ON COLUMN "public"."roster_slots"."driver_id" IS 'Null for a crew place the bus runs with nobody on it yet. A floater always has a driver.';



COMMENT ON COLUMN "public"."roster_slots"."shift" IS 'Crew: the shift driven this month. Floater: the home shift, preferred when choosing cover and not a lock.';



COMMENT ON COLUMN "public"."roster_slots"."rest_weekday" IS 'ISO weekday, 1 Monday to 7 Sunday. Fixed across months.';



COMMENT ON COLUMN "public"."roster_slots"."suggested" IS 'Why auto-fill filled or emptied this place. Null for a place a person set.';



ALTER TABLE "public"."roster_slots" ALTER COLUMN "slot_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."roster_slots_slot_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."routes" (
    "route_id" integer NOT NULL,
    "route_name" character varying(100) NOT NULL,
    "origin" character varying(100) NOT NULL,
    "destination" character varying(100) NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone,
    "waypoints_json" "text",
    "stops_json" "text"
);


ALTER TABLE "public"."routes" OWNER TO "postgres";


ALTER TABLE "public"."routes" ALTER COLUMN "route_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."routes_route_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."schedule_weeks" (
    "week_start" "date" NOT NULL,
    "saved_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "saved_by" integer,
    "roster_only" boolean DEFAULT false NOT NULL
);


ALTER TABLE "public"."schedule_weeks" OWNER TO "postgres";


COMMENT ON COLUMN "public"."schedule_weeks"."saved_by" IS 'The operator who saved this week. Null for a week written by a roster publish rather than by hand.';



CREATE TABLE IF NOT EXISTS "public"."security_incidents" (
    "incident_id" bigint NOT NULL,
    "rule" "text" NOT NULL,
    "severity" "text" NOT NULL,
    "key_column" "text" NOT NULL,
    "key_value" "text" NOT NULL,
    "actions" "text"[] NOT NULL,
    "first_seen_at" timestamp with time zone NOT NULL,
    "last_seen_at" timestamp with time zone NOT NULL,
    "event_count" integer NOT NULL,
    "detected_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "reviewed_at" timestamp with time zone,
    "reviewed_by" "text",
    "review_note" "text",
    "reviewed_event_count" integer,
    "needs_review" boolean GENERATED ALWAYS AS ((("reviewed_at" IS NULL) OR ("event_count" > "reviewed_event_count"))) STORED,
    CONSTRAINT "ck_security_incidents_key" CHECK (("key_column" = ANY (ARRAY['ip'::"text", 'target_id'::"text", 'actor_id'::"text"]))),
    CONSTRAINT "ck_security_incidents_review" CHECK (((("reviewed_at" IS NULL) = ("reviewed_by" IS NULL)) AND (("reviewed_at" IS NULL) = ("reviewed_event_count" IS NULL)))),
    CONSTRAINT "ck_security_incidents_severity" CHECK (("severity" = ANY (ARRAY['low'::"text", 'medium'::"text", 'high'::"text"]))),
    CONSTRAINT "ck_security_incidents_span" CHECK (("last_seen_at" >= "first_seen_at"))
);


ALTER TABLE "public"."security_incidents" OWNER TO "postgres";


COMMENT ON TABLE "public"."security_incidents" IS 'A burst of unusual activity in audit_log, gathered under one row. Holds the filter that finds its entries, never the entries themselves.';



COMMENT ON COLUMN "public"."security_incidents"."rule" IS 'Which rule raised it, by a stable code. The wording shown for a rule lives in the dashboard and may change; this does not.';



COMMENT ON COLUMN "public"."security_incidents"."key_column" IS 'The audit_log column the incident is about: ip for an address, target_id for the account acted on, actor_id for the account acting.';



COMMENT ON COLUMN "public"."security_incidents"."actions" IS 'The audit_log actions the incident gathers. Frozen when it is raised, so a later change to the rule cannot change which entries it shows.';



COMMENT ON COLUMN "public"."security_incidents"."first_seen_at" IS 'The first entry that counted toward crossing the threshold, not the entry that crossed it. Fixed once raised.';



COMMENT ON COLUMN "public"."security_incidents"."last_seen_at" IS 'The latest entry gathered. Extended while activity continues.';



COMMENT ON COLUMN "public"."security_incidents"."event_count" IS 'Entries matching the filter over the span, worked out from audit_log rather than counted up, so seeing an entry twice changes nothing.';



COMMENT ON COLUMN "public"."security_incidents"."reviewed_event_count" IS 'How many entries the reviewer was shown. More than this means activity they have not seen.';



COMMENT ON COLUMN "public"."security_incidents"."needs_review" IS 'Unreviewed, or grown since review. Stored because the rail counts it, and a filter cannot compare two columns.';



ALTER TABLE "public"."security_incidents" ALTER COLUMN "incident_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."security_incidents_incident_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."telemetry_data" (
    "telemetry_id" bigint NOT NULL,
    "trip_id" character varying(20) NOT NULL,
    "latitude" numeric(10,8) NOT NULL,
    "longitude" numeric(11,8) NOT NULL,
    "total_passengers" integer NOT NULL,
    "speed" numeric(5,2),
    "heading" double precision,
    "timestamp" timestamp with time zone DEFAULT "now"() NOT NULL,
    "received_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "accuracy" real,
    CONSTRAINT "ck_telemetry_accuracy" CHECK ((("accuracy" IS NULL) OR ("accuracy" >= (0)::double precision)))
);


ALTER TABLE "public"."telemetry_data" OWNER TO "postgres";


COMMENT ON COLUMN "public"."telemetry_data"."timestamp" IS 'When the fix was taken, by the device clock. Written by the driver app and carried unchanged through its offline queue, so a reading held in a dead zone keeps the time it was actually taken.';



COMMENT ON COLUMN "public"."telemetry_data"."received_at" IS 'When the database accepted the row. With timestamp it separates a reading that was slow to arrive from one that was slow to be taken.';



COMMENT ON COLUMN "public"."telemetry_data"."accuracy" IS 'Radius in metres within which the phone places the fix, as Android reports it. Null from builds that do not send it.';



ALTER TABLE "public"."telemetry_data" ALTER COLUMN "telemetry_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."telemetry_data_telemetry_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE SEQUENCE IF NOT EXISTS "public"."trip_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE "public"."trip_id_seq" OWNER TO "postgres";


CREATE SEQUENCE IF NOT EXISTS "public"."trip_seq"
    START WITH 26001
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE "public"."trip_seq" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."trips" (
    "trip_id" character varying(20) DEFAULT ('TRIP'::"text" || "lpad"(("nextval"('"public"."trip_seq"'::"regclass"))::"text", 6, '0'::"text")) NOT NULL,
    "date" "date" NOT NULL,
    "shift_type" character varying(50) NOT NULL,
    "shift_start_time" time without time zone NOT NULL,
    "shift_end_time" time without time zone NOT NULL,
    "route_id" integer NOT NULL,
    "vehicle_id" character varying(20) NOT NULL,
    "driver_id" integer NOT NULL,
    "trip_status" "public"."trip_status_enum" DEFAULT 'Not Yet Started'::"public"."trip_status_enum" NOT NULL,
    "estimated_revenue" numeric(10,2) DEFAULT 0.00 NOT NULL,
    "total_boarded" integer DEFAULT 0 NOT NULL,
    "actual_start_time" timestamp with time zone,
    "actual_end_time" timestamp with time zone,
    "is_simulated" boolean DEFAULT false NOT NULL,
    "count_heartbeat" timestamp with time zone,
    "counter_device_id" "text",
    "break_start" time without time zone,
    "roster_month" "date",
    "hand_edited" boolean DEFAULT false NOT NULL,
    CONSTRAINT "trips_break_start_in_shift" CHECK ((("break_start" IS NULL) OR ((("break_start" = ("shift_start_time" + '03:00:00'::interval)) OR ("break_start" = ("shift_start_time" + '04:00:00'::interval))) OR ("break_start" = ("shift_start_time" + '05:00:00'::interval)))))
);


ALTER TABLE "public"."trips" OWNER TO "postgres";


COMMENT ON COLUMN "public"."trips"."break_start" IS 'Philippine wall-clock start of the one hour break, 3, 4 or 5 hours after shift_start_time. Null on trips written before breaks existed.';



COMMENT ON COLUMN "public"."trips"."roster_month" IS 'The roster month that wrote this trip, or null for a trip made by hand.';



COMMENT ON COLUMN "public"."trips"."hand_edited" IS 'True once the driver, bus, route, shift, date or break of a roster trip was changed outside a publish. Set by trg_trips_roster_edit; a re-publish leaves such a trip alone.';



CREATE TABLE IF NOT EXISTS "public"."users" (
    "user_id" integer NOT NULL,
    "first_name" character varying(50) NOT NULL,
    "middle_name" character varying(50),
    "last_name" character varying(50) NOT NULL,
    "email_address" character varying(100) NOT NULL,
    "password_hash" character varying(255) NOT NULL,
    "role_id" integer NOT NULL,
    "account_status" "public"."account_status_enum" DEFAULT 'Activated'::"public"."account_status_enum" NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone,
    "last_login" timestamp with time zone,
    "contact_number" "text",
    "address" "text",
    "emergency_contact_name" "text",
    "emergency_contact_number" "text"
);


ALTER TABLE "public"."users" OWNER TO "postgres";


CREATE OR REPLACE VIEW "public"."users_app" AS
 SELECT "user_id",
    "first_name",
    "middle_name",
    "last_name",
    "email_address",
    "role_id",
    "account_status",
    "contact_number",
    "address",
    "emergency_contact_name",
    "emergency_contact_number",
    "created_at",
    "updated_at",
    "last_login"
   FROM "public"."users"
  WHERE ("user_id" = COALESCE("public"."jwt_uid"(), "user_id"))
  WITH CASCADED CHECK OPTION;


ALTER VIEW "public"."users_app" OWNER TO "postgres";


ALTER TABLE "public"."users" ALTER COLUMN "user_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."users_user_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."vehicles" (
    "vehicle_id" character varying(20) NOT NULL,
    "plate_number" character varying(20) NOT NULL,
    "route_id" integer,
    "capacity" integer NOT NULL,
    "vehicle_status" "public"."vehicle_status_enum" DEFAULT 'Ready to Deploy'::"public"."vehicle_status_enum" NOT NULL,
    "last_maintenance_date" "date",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone,
    "out_of_service" boolean DEFAULT false NOT NULL,
    "counter_device_id" "text",
    "retired_at" timestamp with time zone,
    "retired_reason" "text"
);


ALTER TABLE "public"."vehicles" OWNER TO "postgres";


COMMENT ON COLUMN "public"."vehicles"."retired_at" IS 'When the bus left the fleet for good. Null means it is still in service.';



COMMENT ON COLUMN "public"."vehicles"."retired_reason" IS 'Why it was retired, as entered by an administrator.';



ALTER TABLE ONLY "public"."audit_log"
    ADD CONSTRAINT "audit_log_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."bus_checklist"
    ADD CONSTRAINT "bus_checklist_pkey" PRIMARY KEY ("checklist_id");



ALTER TABLE ONLY "public"."checklist_items"
    ADD CONSTRAINT "checklist_items_pkey" PRIMARY KEY ("item_id");



ALTER TABLE ONLY "public"."checklist_items"
    ADD CONSTRAINT "checklist_items_section_key_label_key" UNIQUE ("section_key", "label");



ALTER TABLE ONLY "public"."device_config"
    ADD CONSTRAINT "device_config_pkey" PRIMARY KEY ("device_id");



ALTER TABLE ONLY "public"."device_status"
    ADD CONSTRAINT "device_status_pkey" PRIMARY KEY ("device_id");



ALTER TABLE ONLY "public"."driver_availability"
    ADD CONSTRAINT "driver_availability_pkey" PRIMARY KEY ("user_id");



ALTER TABLE ONLY "public"."fare_config"
    ADD CONSTRAINT "fare_config_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."leave_requests"
    ADD CONSTRAINT "leave_requests_pkey" PRIMARY KEY ("request_id");



ALTER TABLE ONLY "public"."maintenance_items"
    ADD CONSTRAINT "maintenance_items_pkey" PRIMARY KEY ("item_id");



ALTER TABLE ONLY "public"."maintenance_logs"
    ADD CONSTRAINT "maintenance_logs_pkey" PRIMARY KEY ("log_id");



ALTER TABLE ONLY "public"."maintenance_notes"
    ADD CONSTRAINT "maintenance_notes_pkey" PRIMARY KEY ("note_id");



ALTER TABLE ONLY "public"."messages"
    ADD CONSTRAINT "messages_pkey" PRIMARY KEY ("message_id");



ALTER TABLE ONLY "public"."password_reset_otp"
    ADD CONSTRAINT "password_reset_otp_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."boarding_events"
    ADD CONSTRAINT "pk_boarding_events" PRIMARY KEY ("event_id");



ALTER TABLE ONLY "public"."device_health_log"
    ADD CONSTRAINT "pk_device_health_log" PRIMARY KEY ("log_id");



ALTER TABLE ONLY "public"."inspection_photos"
    ADD CONSTRAINT "pk_inspection_photos" PRIMARY KEY ("photo_id");



ALTER TABLE ONLY "public"."security_incidents"
    ADD CONSTRAINT "pk_security_incidents" PRIMARY KEY ("incident_id");



ALTER TABLE ONLY "public"."roles"
    ADD CONSTRAINT "roles_pkey" PRIMARY KEY ("role_id");



ALTER TABLE ONLY "public"."roster_gaps"
    ADD CONSTRAINT "roster_gaps_pkey" PRIMARY KEY ("date", "vehicle_id", "shift");



ALTER TABLE ONLY "public"."roster_holds"
    ADD CONSTRAINT "roster_holds_pkey" PRIMARY KEY ("month", "driver_id");



ALTER TABLE ONLY "public"."roster_months"
    ADD CONSTRAINT "roster_months_pkey" PRIMARY KEY ("month");



ALTER TABLE ONLY "public"."roster_skips"
    ADD CONSTRAINT "roster_skips_pkey" PRIMARY KEY ("date", "vehicle_id", "shift");



ALTER TABLE ONLY "public"."roster_slots"
    ADD CONSTRAINT "roster_slots_pkey" PRIMARY KEY ("slot_id");



ALTER TABLE ONLY "public"."routes"
    ADD CONSTRAINT "routes_pkey" PRIMARY KEY ("route_id");



ALTER TABLE ONLY "public"."schedule_weeks"
    ADD CONSTRAINT "schedule_weeks_pkey" PRIMARY KEY ("week_start");



ALTER TABLE ONLY "public"."telemetry_data"
    ADD CONSTRAINT "telemetry_data_pkey" PRIMARY KEY ("telemetry_id");



ALTER TABLE ONLY "public"."trips"
    ADD CONSTRAINT "trips_pkey" PRIMARY KEY ("trip_id");



ALTER TABLE ONLY "public"."security_incidents"
    ADD CONSTRAINT "uq_security_incidents_episode" UNIQUE ("rule", "key_value", "first_seen_at");



ALTER TABLE ONLY "public"."users"
    ADD CONSTRAINT "users_email_address_key" UNIQUE ("email_address");



ALTER TABLE ONLY "public"."users"
    ADD CONSTRAINT "users_pkey" PRIMARY KEY ("user_id");



ALTER TABLE ONLY "public"."vehicles"
    ADD CONSTRAINT "vehicles_pkey" PRIMARY KEY ("vehicle_id");



ALTER TABLE ONLY "public"."vehicles"
    ADD CONSTRAINT "vehicles_plate_number_key" UNIQUE ("plate_number");



CREATE INDEX "idx_audit_action" ON "public"."audit_log" USING "btree" ("action", "occurred_at" DESC);



CREATE INDEX "idx_audit_actor" ON "public"."audit_log" USING "btree" ("actor_id", "occurred_at" DESC);



CREATE INDEX "idx_audit_target" ON "public"."audit_log" USING "btree" ("target_table", "target_id");



CREATE INDEX "idx_audit_time" ON "public"."audit_log" USING "btree" ("occurred_at" DESC);



CREATE INDEX "idx_checklist_items_order" ON "public"."checklist_items" USING "btree" ("active", "sort_order");



CREATE INDEX "idx_maintenance_items_open" ON "public"."maintenance_items" USING "btree" ("log_id") WHERE ("state" = 'open'::"text");



CREATE INDEX "idx_pwreset_ip" ON "public"."password_reset_otp" USING "btree" ("ip", "created_at" DESC);



CREATE INDEX "idx_pwreset_time" ON "public"."password_reset_otp" USING "btree" ("created_at" DESC);



CREATE INDEX "idx_pwreset_user" ON "public"."password_reset_otp" USING "btree" ("user_id", "created_at" DESC);



CREATE INDEX "idx_vehicles_retired" ON "public"."vehicles" USING "btree" ("retired_at");



CREATE INDEX "ix_boarding_events_device_timestamp" ON "public"."boarding_events" USING "btree" ("device_timestamp");



CREATE INDEX "ix_boarding_events_trip" ON "public"."boarding_events" USING "btree" ("trip_id", "direction");



CREATE INDEX "ix_device_health_log_device_occurred" ON "public"."device_health_log" USING "btree" ("device_id", "occurred_at");



CREATE INDEX "ix_inspection_photos_item" ON "public"."inspection_photos" USING "btree" ("checklist_item_id");



CREATE INDEX "ix_inspection_photos_log" ON "public"."inspection_photos" USING "btree" ("log_id");



CREATE INDEX "ix_maintenance_items_log_checklist_item" ON "public"."maintenance_items" USING "btree" ("log_id", "checklist_item_id");



CREATE INDEX "ix_maintenance_notes_log" ON "public"."maintenance_notes" USING "btree" ("log_id");



CREATE INDEX "ix_security_incidents_key" ON "public"."security_incidents" USING "btree" ("rule", "key_value", "last_seen_at" DESC);



CREATE INDEX "ix_security_incidents_review" ON "public"."security_incidents" USING "btree" ("needs_review", "last_seen_at" DESC);



CREATE INDEX "leave_requests_status_idx" ON "public"."leave_requests" USING "btree" ("status", "start_date");



CREATE INDEX "leave_requests_user_id_idx" ON "public"."leave_requests" USING "btree" ("user_id", "start_date");



CREATE INDEX "roster_gaps_month_idx" ON "public"."roster_gaps" USING "btree" ("month");



CREATE INDEX "roster_skips_month_idx" ON "public"."roster_skips" USING "btree" ("month");



CREATE UNIQUE INDEX "roster_slots_one_crew_per_bus_shift" ON "public"."roster_slots" USING "btree" ("month", "vehicle_id", "shift") WHERE ("kind" = 'Crew'::"text");



CREATE UNIQUE INDEX "roster_slots_one_place_per_driver" ON "public"."roster_slots" USING "btree" ("month", "driver_id");



CREATE INDEX "trips_roster_month_idx" ON "public"."trips" USING "btree" ("roster_month") WHERE ("roster_month" IS NOT NULL);



CREATE UNIQUE INDEX "uq_maintenance_items_label" ON "public"."maintenance_items" USING "btree" ("log_id", "lower"("label"));



CREATE OR REPLACE TRIGGER "trg_audit_devcfg_ins_del" AFTER INSERT OR DELETE ON "public"."device_config" FOR EACH ROW EXECUTE FUNCTION "public"."audit_row_change"();



CREATE OR REPLACE TRIGGER "trg_audit_devcfg_upd" AFTER UPDATE ON "public"."device_config" FOR EACH ROW WHEN (((("to_jsonb"("old".*) - 'wake_requested_at'::"text") - 'updated_at'::"text") IS DISTINCT FROM (("to_jsonb"("new".*) - 'wake_requested_at'::"text") - 'updated_at'::"text"))) EXECUTE FUNCTION "public"."audit_row_change"();



CREATE OR REPLACE TRIGGER "trg_audit_log_no_delete" BEFORE DELETE ON "public"."audit_log" FOR EACH ROW EXECUTE FUNCTION "public"."audit_log_immutable"();



CREATE OR REPLACE TRIGGER "trg_audit_log_no_truncate" BEFORE TRUNCATE ON "public"."audit_log" FOR EACH STATEMENT EXECUTE FUNCTION "public"."audit_log_immutable"();



CREATE OR REPLACE TRIGGER "trg_audit_log_no_update" BEFORE UPDATE ON "public"."audit_log" FOR EACH ROW EXECUTE FUNCTION "public"."audit_log_immutable"();



CREATE OR REPLACE TRIGGER "trg_audit_users_ins_del" AFTER INSERT OR DELETE ON "public"."users" FOR EACH ROW EXECUTE FUNCTION "public"."audit_row_change"();



CREATE OR REPLACE TRIGGER "trg_audit_users_pwd" AFTER UPDATE OF "password_hash" ON "public"."users" FOR EACH ROW WHEN ((("old"."password_hash")::"text" IS DISTINCT FROM ("new"."password_hash")::"text")) EXECUTE FUNCTION "public"."audit_password_change"();



CREATE OR REPLACE TRIGGER "trg_audit_users_upd" AFTER UPDATE ON "public"."users" FOR EACH ROW WHEN ((((("to_jsonb"("old".*) - 'password_hash'::"text") - 'last_login'::"text") - 'updated_at'::"text") IS DISTINCT FROM ((("to_jsonb"("new".*) - 'password_hash'::"text") - 'last_login'::"text") - 'updated_at'::"text"))) EXECUTE FUNCTION "public"."audit_row_change"();



CREATE OR REPLACE TRIGGER "trg_schedule_weeks_roster_only" BEFORE INSERT OR UPDATE ON "public"."schedule_weeks" FOR EACH ROW EXECUTE FUNCTION "public"."schedule_weeks_roster_only"();



CREATE OR REPLACE TRIGGER "trg_trips_closed_guard" BEFORE UPDATE ON "public"."trips" FOR EACH ROW WHEN (("old"."trip_status" = 'Completed'::"public"."trip_status_enum")) EXECUTE FUNCTION "public"."trips_closed_guard"();



COMMENT ON TRIGGER "trg_trips_closed_guard" ON "public"."trips" IS 'Keeps a completed trip as it was closed, whatever a driver phone still holding it sends.';



CREATE OR REPLACE TRIGGER "trg_trips_count_guard" BEFORE UPDATE ON "public"."trips" FOR EACH ROW WHEN ((("old"."total_boarded" IS DISTINCT FROM "new"."total_boarded") OR ("old"."estimated_revenue" IS DISTINCT FROM "new"."estimated_revenue") OR ("old"."trip_status" IS DISTINCT FROM "new"."trip_status"))) EXECUTE FUNCTION "public"."trips_count_guard"();



CREATE OR REPLACE TRIGGER "trg_trips_roster_edit" BEFORE UPDATE ON "public"."trips" FOR EACH ROW WHEN ((("old"."roster_month" IS NOT NULL) AND (NOT "old"."hand_edited") AND (("old"."driver_id" IS DISTINCT FROM "new"."driver_id") OR (("old"."vehicle_id")::"text" IS DISTINCT FROM ("new"."vehicle_id")::"text") OR ("old"."route_id" IS DISTINCT FROM "new"."route_id") OR (("old"."shift_type")::"text" IS DISTINCT FROM ("new"."shift_type")::"text") OR ("old"."date" IS DISTINCT FROM "new"."date") OR ("old"."break_start" IS DISTINCT FROM "new"."break_start")))) EXECUTE FUNCTION "public"."trips_roster_guard"();



CREATE OR REPLACE TRIGGER "trg_trips_roster_skip" AFTER DELETE ON "public"."trips" FOR EACH ROW WHEN (("old"."roster_month" IS NOT NULL)) EXECUTE FUNCTION "public"."trips_roster_guard"();



CREATE OR REPLACE TRIGGER "trg_trips_takeover_guard" BEFORE UPDATE ON "public"."trips" FOR EACH ROW WHEN ((("new"."trip_status" = 'Active'::"public"."trip_status_enum") AND ("old"."trip_status" IS DISTINCT FROM "new"."trip_status"))) EXECUTE FUNCTION "public"."trips_takeover_guard"();



COMMENT ON TRIGGER "trg_trips_takeover_guard" ON "public"."trips" IS 'Settles the handover when a trip starts on a bus that is already on another active trip.';



CREATE OR REPLACE TRIGGER "trg_users_email_lower" BEFORE INSERT OR UPDATE OF "email_address" ON "public"."users" FOR EACH ROW EXECUTE FUNCTION "public"."users_email_lower"();



CREATE OR REPLACE TRIGGER "trg_vehicles_release_guard" BEFORE UPDATE ON "public"."vehicles" FOR EACH ROW WHEN (("old"."vehicle_status" IS DISTINCT FROM "new"."vehicle_status")) EXECUTE FUNCTION "public"."vehicles_release_guard"();



COMMENT ON TRIGGER "trg_vehicles_release_guard" ON "public"."vehicles" IS 'Stops a driver phone from releasing a bus somebody else is driving.';



ALTER TABLE ONLY "public"."bus_checklist"
    ADD CONSTRAINT "bus_checklist_driver_id_fkey" FOREIGN KEY ("driver_id") REFERENCES "public"."users"("user_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."bus_checklist"
    ADD CONSTRAINT "bus_checklist_trip_id_fkey" FOREIGN KEY ("trip_id") REFERENCES "public"."trips"("trip_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."bus_checklist"
    ADD CONSTRAINT "bus_checklist_vehicle_id_fkey" FOREIGN KEY ("vehicle_id") REFERENCES "public"."vehicles"("vehicle_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."driver_availability"
    ADD CONSTRAINT "driver_availability_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "public"."users"("user_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."boarding_events"
    ADD CONSTRAINT "fk_boarding_events_trip" FOREIGN KEY ("trip_id") REFERENCES "public"."trips"("trip_id");



ALTER TABLE ONLY "public"."device_health_log"
    ADD CONSTRAINT "fk_device_health_log_trip" FOREIGN KEY ("trip_id") REFERENCES "public"."trips"("trip_id");



ALTER TABLE ONLY "public"."inspection_photos"
    ADD CONSTRAINT "fk_inspection_photos_checklist" FOREIGN KEY ("checklist_id") REFERENCES "public"."bus_checklist"("checklist_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."inspection_photos"
    ADD CONSTRAINT "fk_inspection_photos_item" FOREIGN KEY ("checklist_item_id") REFERENCES "public"."checklist_items"("item_id");



ALTER TABLE ONLY "public"."inspection_photos"
    ADD CONSTRAINT "fk_inspection_photos_log" FOREIGN KEY ("log_id") REFERENCES "public"."maintenance_logs"("log_id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."maintenance_items"
    ADD CONSTRAINT "fk_maintenance_items_checklist_item" FOREIGN KEY ("checklist_item_id") REFERENCES "public"."checklist_items"("item_id");



ALTER TABLE ONLY "public"."schedule_weeks"
    ADD CONSTRAINT "fk_schedule_weeks_saved_by" FOREIGN KEY ("saved_by") REFERENCES "public"."users"("user_id");



ALTER TABLE ONLY "public"."leave_requests"
    ADD CONSTRAINT "leave_requests_decided_by_fkey" FOREIGN KEY ("decided_by") REFERENCES "public"."users"("user_id");



ALTER TABLE ONLY "public"."leave_requests"
    ADD CONSTRAINT "leave_requests_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "public"."users"("user_id");



ALTER TABLE ONLY "public"."leave_requests"
    ADD CONSTRAINT "leave_requests_withdraw_answered_by_fkey" FOREIGN KEY ("withdraw_answered_by") REFERENCES "public"."users"("user_id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."maintenance_items"
    ADD CONSTRAINT "maintenance_items_log_id_fkey" FOREIGN KEY ("log_id") REFERENCES "public"."maintenance_logs"("log_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."maintenance_logs"
    ADD CONSTRAINT "maintenance_logs_checklist_id_fkey" FOREIGN KEY ("checklist_id") REFERENCES "public"."bus_checklist"("checklist_id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."maintenance_logs"
    ADD CONSTRAINT "maintenance_logs_trip_id_fkey" FOREIGN KEY ("trip_id") REFERENCES "public"."trips"("trip_id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."maintenance_logs"
    ADD CONSTRAINT "maintenance_logs_vehicle_id_fkey" FOREIGN KEY ("vehicle_id") REFERENCES "public"."vehicles"("vehicle_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."maintenance_notes"
    ADD CONSTRAINT "maintenance_notes_author_id_fkey" FOREIGN KEY ("author_id") REFERENCES "public"."users"("user_id");



ALTER TABLE ONLY "public"."maintenance_notes"
    ADD CONSTRAINT "maintenance_notes_log_id_fkey" FOREIGN KEY ("log_id") REFERENCES "public"."maintenance_logs"("log_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."messages"
    ADD CONSTRAINT "messages_sender_id_fkey" FOREIGN KEY ("sender_id") REFERENCES "public"."users"("user_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."password_reset_otp"
    ADD CONSTRAINT "password_reset_otp_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "public"."users"("user_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."roster_gaps"
    ADD CONSTRAINT "roster_gaps_month_fkey" FOREIGN KEY ("month") REFERENCES "public"."roster_months"("month") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."roster_gaps"
    ADD CONSTRAINT "roster_gaps_route_id_fkey" FOREIGN KEY ("route_id") REFERENCES "public"."routes"("route_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."roster_gaps"
    ADD CONSTRAINT "roster_gaps_vehicle_id_fkey" FOREIGN KEY ("vehicle_id") REFERENCES "public"."vehicles"("vehicle_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."roster_holds"
    ADD CONSTRAINT "roster_holds_driver_fkey" FOREIGN KEY ("driver_id") REFERENCES "public"."users"("user_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."roster_holds"
    ADD CONSTRAINT "roster_holds_month_fkey" FOREIGN KEY ("month") REFERENCES "public"."roster_months"("month") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."roster_months"
    ADD CONSTRAINT "roster_months_saved_by_fkey" FOREIGN KEY ("saved_by") REFERENCES "public"."users"("user_id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."roster_skips"
    ADD CONSTRAINT "roster_skips_month_fkey" FOREIGN KEY ("month") REFERENCES "public"."roster_months"("month") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."roster_skips"
    ADD CONSTRAINT "roster_skips_vehicle_id_fkey" FOREIGN KEY ("vehicle_id") REFERENCES "public"."vehicles"("vehicle_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."roster_slots"
    ADD CONSTRAINT "roster_slots_driver_id_fkey" FOREIGN KEY ("driver_id") REFERENCES "public"."users"("user_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."roster_slots"
    ADD CONSTRAINT "roster_slots_month_fkey" FOREIGN KEY ("month") REFERENCES "public"."roster_months"("month") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."roster_slots"
    ADD CONSTRAINT "roster_slots_route_id_fkey" FOREIGN KEY ("route_id") REFERENCES "public"."routes"("route_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."roster_slots"
    ADD CONSTRAINT "roster_slots_vehicle_id_fkey" FOREIGN KEY ("vehicle_id") REFERENCES "public"."vehicles"("vehicle_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."telemetry_data"
    ADD CONSTRAINT "telemetry_data_trip_id_fkey" FOREIGN KEY ("trip_id") REFERENCES "public"."trips"("trip_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."trips"
    ADD CONSTRAINT "trips_driver_id_fkey" FOREIGN KEY ("driver_id") REFERENCES "public"."users"("user_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."trips"
    ADD CONSTRAINT "trips_roster_month_fkey" FOREIGN KEY ("roster_month") REFERENCES "public"."roster_months"("month") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."trips"
    ADD CONSTRAINT "trips_route_id_fkey" FOREIGN KEY ("route_id") REFERENCES "public"."routes"("route_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."trips"
    ADD CONSTRAINT "trips_vehicle_id_fkey" FOREIGN KEY ("vehicle_id") REFERENCES "public"."vehicles"("vehicle_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."users"
    ADD CONSTRAINT "users_role_id_fkey" FOREIGN KEY ("role_id") REFERENCES "public"."roles"("role_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."vehicles"
    ADD CONSTRAINT "vehicles_route_id_fkey" FOREIGN KEY ("route_id") REFERENCES "public"."routes"("route_id") ON DELETE SET NULL;



CREATE POLICY "app full access" ON "public"."maintenance_notes" USING (true) WITH CHECK (true);



ALTER TABLE "public"."audit_log" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."boarding_events" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."bus_checklist" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."checklist_items" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "checklist_items_read" ON "public"."checklist_items" FOR SELECT TO "app_driver" USING ("active");



ALTER TABLE "public"."device_config" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."device_health_log" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."device_status" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."driver_availability" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."fare_config" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."inspection_photos" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."leave_requests" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "leave_requests_update" ON "public"."leave_requests" FOR UPDATE USING (true) WITH CHECK (true);



ALTER TABLE "public"."maintenance_items" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."maintenance_logs" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."maintenance_notes" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."messages" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "p_avail_driver_all" ON "public"."driver_availability" TO "app_driver" USING (("user_id" = "public"."jwt_uid"())) WITH CHECK (("user_id" = "public"."jwt_uid"()));



CREATE POLICY "p_boarding_insert_own_device" ON "public"."boarding_events" FOR INSERT TO "app_camera" WITH CHECK (("counter_device_id" = ((NULLIF("current_setting"('request.jwt.claims'::"text", true), ''::"text"))::json ->> 'device_id'::"text")));



CREATE POLICY "p_boarding_select_own_device" ON "public"."boarding_events" FOR SELECT TO "app_camera" USING (("counter_device_id" = ((NULLIF("current_setting"('request.jwt.claims'::"text", true), ''::"text"))::json ->> 'device_id'::"text")));



CREATE POLICY "p_checklists_driver_insert" ON "public"."bus_checklist" FOR INSERT TO "app_driver" WITH CHECK ((EXISTS ( SELECT 1
   FROM "public"."trips" "t"
  WHERE ((("t"."trip_id")::"text" = ("bus_checklist"."trip_id")::"text") AND ("t"."driver_id" = "public"."jwt_uid"())))));



CREATE POLICY "p_checklists_driver_select" ON "public"."bus_checklist" FOR SELECT TO "app_driver" USING ((EXISTS ( SELECT 1
   FROM "public"."trips" "t"
  WHERE ((("t"."trip_id")::"text" = ("bus_checklist"."trip_id")::"text") AND ("t"."driver_id" = "public"."jwt_uid"())))));



CREATE POLICY "p_devcfg_camera_all" ON "public"."device_config" TO "app_camera" USING (("device_id" = "public"."jwt_dev"())) WITH CHECK (("device_id" = "public"."jwt_dev"()));



CREATE POLICY "p_devcfg_driver_select" ON "public"."device_config" FOR SELECT TO "app_driver" USING (("device_id" = "public"."driver_active_camera"()));



CREATE POLICY "p_devcfg_driver_update" ON "public"."device_config" FOR UPDATE TO "app_driver" USING ((("device_id" = "public"."driver_active_camera"()) AND "public"."driver_is_active"())) WITH CHECK (("device_id" = "public"."driver_active_camera"()));



CREATE POLICY "p_device_health_insert_own_device" ON "public"."device_health_log" FOR INSERT TO "app_camera" WITH CHECK (("device_id" = ((NULLIF("current_setting"('request.jwt.claims'::"text", true), ''::"text"))::json ->> 'device_id'::"text")));



CREATE POLICY "p_devstat_camera_all" ON "public"."device_status" TO "app_camera" USING (("device_id" = "public"."jwt_dev"())) WITH CHECK (("device_id" = "public"."jwt_dev"()));



CREATE POLICY "p_devstat_driver_select" ON "public"."device_status" FOR SELECT TO "app_driver" USING (("device_id" = "public"."driver_active_camera"()));



CREATE POLICY "p_fare_driver_select" ON "public"."fare_config" FOR SELECT TO "app_driver" USING (true);



CREATE POLICY "p_leave_driver_own" ON "public"."leave_requests" TO "app_driver" USING (("user_id" = "public"."jwt_uid"())) WITH CHECK (("user_id" = "public"."jwt_uid"()));



CREATE POLICY "p_leave_driver_update" ON "public"."leave_requests" FOR UPDATE TO "app_driver" USING ((("user_id" = "public"."jwt_uid"()) AND (("status")::"text" = ANY ((ARRAY['Pending'::character varying, 'AwaitingChange'::character varying])::"text"[])))) WITH CHECK ((("user_id" = "public"."jwt_uid"()) AND (("status")::"text" = 'Cancelled'::"text")));



CREATE POLICY "p_maintenance_driver_insert" ON "public"."maintenance_logs" FOR INSERT TO "app_driver" WITH CHECK ((EXISTS ( SELECT 1
   FROM "public"."trips" "t"
  WHERE ((("t"."trip_id")::"text" = ("maintenance_logs"."trip_id")::"text") AND ("t"."driver_id" = "public"."jwt_uid"())))));



CREATE POLICY "p_messages_driver_select" ON "public"."messages" FOR SELECT TO "app_driver" USING ((("lower"(COALESCE(("target_audience")::"text", ''::"text")) = 'all'::"text") OR (("lower"(("target_audience")::"text") = 'driver'::"text") AND (("target_id")::"text" = ("public"."jwt_uid"())::"text")) OR (("lower"(("target_audience")::"text") = 'route'::"text") AND (("target_id")::"text" IN ( SELECT ("t"."route_id")::"text" AS "route_id"
   FROM "public"."trips" "t"
  WHERE ("t"."driver_id" = "public"."jwt_uid"()))))));



CREATE POLICY "p_messages_driver_update" ON "public"."messages" FOR UPDATE TO "app_driver" USING ((("lower"(("target_audience")::"text") = 'driver'::"text") AND (("target_id")::"text" = ("public"."jwt_uid"())::"text")));



CREATE POLICY "p_roster_months_driver_select" ON "public"."roster_months" FOR SELECT TO "app_driver" USING (("status" = 'Published'::"text"));



CREATE POLICY "p_roster_slots_driver_select" ON "public"."roster_slots" FOR SELECT TO "app_driver" USING ((("driver_id" = "public"."jwt_uid"()) AND (EXISTS ( SELECT 1
   FROM "public"."roster_months" "m"
  WHERE (("m"."month" = "roster_slots"."month") AND ("m"."status" = 'Published'::"text"))))));



CREATE POLICY "p_routes_driver_select" ON "public"."routes" FOR SELECT TO "app_driver" USING (true);



CREATE POLICY "p_schedule_weeks_driver_select" ON "public"."schedule_weeks" FOR SELECT TO "app_driver" USING (true);



CREATE POLICY "p_telemetry_driver_insert" ON "public"."telemetry_data" FOR INSERT TO "app_driver" WITH CHECK ((EXISTS ( SELECT 1
   FROM "public"."trips" "t"
  WHERE ((("t"."trip_id")::"text" = ("telemetry_data"."trip_id")::"text") AND ("t"."driver_id" = "public"."jwt_uid"())))));



CREATE POLICY "p_trips_camera_select" ON "public"."trips" FOR SELECT TO "app_camera" USING (((("vehicle_id")::"text" = "public"."camera_vehicle"()) OR ("counter_device_id" = "public"."jwt_dev"())));



CREATE POLICY "p_trips_camera_update" ON "public"."trips" FOR UPDATE TO "app_camera" USING ((((("vehicle_id")::"text" = "public"."camera_vehicle"()) AND ("trip_status" = 'Active'::"public"."trip_status_enum")) OR ("counter_device_id" = "public"."jwt_dev"()))) WITH CHECK (("counter_device_id" = "public"."jwt_dev"()));



CREATE POLICY "p_trips_driver_select" ON "public"."trips" FOR SELECT TO "app_driver" USING (("driver_id" = "public"."jwt_uid"()));



CREATE POLICY "p_trips_driver_update" ON "public"."trips" FOR UPDATE TO "app_driver" USING ((("driver_id" = "public"."jwt_uid"()) AND "public"."driver_is_active"())) WITH CHECK (("driver_id" = "public"."jwt_uid"()));



CREATE POLICY "p_vehicles_camera_select" ON "public"."vehicles" FOR SELECT TO "app_camera" USING (true);



CREATE POLICY "p_vehicles_camera_update" ON "public"."vehicles" FOR UPDATE TO "app_camera" USING ((("counter_device_id" IS NULL) OR ("counter_device_id" = "public"."jwt_dev"()))) WITH CHECK ((("counter_device_id" = "public"."jwt_dev"()) OR ("counter_device_id" IS NULL)));



CREATE POLICY "p_vehicles_driver_select" ON "public"."vehicles" FOR SELECT TO "app_driver" USING (true);



CREATE POLICY "p_vehicles_driver_update" ON "public"."vehicles" FOR UPDATE TO "app_driver" USING ((EXISTS ( SELECT 1
   FROM "public"."trips" "t"
  WHERE ((("t"."vehicle_id")::"text" = ("vehicles"."vehicle_id")::"text") AND ("t"."driver_id" = "public"."jwt_uid"())))));



ALTER TABLE "public"."password_reset_otp" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."roster_gaps" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."roster_holds" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."roster_months" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."roster_skips" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."roster_slots" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."routes" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."schedule_weeks" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."security_incidents" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."telemetry_data" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."trips" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."users" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."vehicles" ENABLE ROW LEVEL SECURITY;




ALTER PUBLICATION "supabase_realtime" OWNER TO "postgres";


GRANT USAGE ON SCHEMA "public" TO "postgres";
GRANT USAGE ON SCHEMA "public" TO "anon";
GRANT USAGE ON SCHEMA "public" TO "authenticated";
GRANT USAGE ON SCHEMA "public" TO "service_role";
GRANT USAGE ON SCHEMA "public" TO "app_driver";
GRANT USAGE ON SCHEMA "public" TO "app_camera";






















































































































































GRANT ALL ON FUNCTION "public"."audit_log_immutable"() TO "anon";
GRANT ALL ON FUNCTION "public"."audit_log_immutable"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."audit_log_immutable"() TO "service_role";



GRANT ALL ON FUNCTION "public"."audit_password_change"() TO "anon";
GRANT ALL ON FUNCTION "public"."audit_password_change"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."audit_password_change"() TO "service_role";



GRANT ALL ON FUNCTION "public"."audit_row_change"() TO "anon";
GRANT ALL ON FUNCTION "public"."audit_row_change"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."audit_row_change"() TO "service_role";



REVOKE ALL ON FUNCTION "public"."boardings_by_hour"("p_trip_ids" "text"[]) FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."boardings_by_hour"("p_trip_ids" "text"[]) TO "service_role";



GRANT ALL ON FUNCTION "public"."camera_vehicle"() TO "anon";
GRANT ALL ON FUNCTION "public"."camera_vehicle"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."camera_vehicle"() TO "service_role";
GRANT ALL ON FUNCTION "public"."camera_vehicle"() TO "app_driver";
GRANT ALL ON FUNCTION "public"."camera_vehicle"() TO "app_camera";



REVOKE ALL ON FUNCTION "public"."dashboard_figures"("p_today" "date", "p_yesterday_active_only" boolean) FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."dashboard_figures"("p_today" "date", "p_yesterday_active_only" boolean) TO "service_role";



GRANT ALL ON FUNCTION "public"."driver_active_camera"() TO "anon";
GRANT ALL ON FUNCTION "public"."driver_active_camera"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."driver_active_camera"() TO "service_role";
GRANT ALL ON FUNCTION "public"."driver_active_camera"() TO "app_driver";



GRANT ALL ON FUNCTION "public"."driver_is_active"() TO "anon";
GRANT ALL ON FUNCTION "public"."driver_is_active"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."driver_is_active"() TO "service_role";
GRANT ALL ON FUNCTION "public"."driver_is_active"() TO "app_driver";
GRANT ALL ON FUNCTION "public"."driver_is_active"() TO "app_camera";



REVOKE ALL ON FUNCTION "public"."fleetmap_live"("p_op_day" "date", "p_route_id" integer, "p_since" timestamp with time zone, "p_seen" "jsonb") FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."fleetmap_live"("p_op_day" "date", "p_route_id" integer, "p_since" timestamp with time zone, "p_seen" "jsonb") TO "service_role";



GRANT ALL ON FUNCTION "public"."jwt_dev"() TO "anon";
GRANT ALL ON FUNCTION "public"."jwt_dev"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."jwt_dev"() TO "service_role";
GRANT ALL ON FUNCTION "public"."jwt_dev"() TO "app_driver";
GRANT ALL ON FUNCTION "public"."jwt_dev"() TO "app_camera";



GRANT ALL ON FUNCTION "public"."jwt_uid"() TO "anon";
GRANT ALL ON FUNCTION "public"."jwt_uid"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."jwt_uid"() TO "service_role";
GRANT ALL ON FUNCTION "public"."jwt_uid"() TO "app_driver";
GRANT ALL ON FUNCTION "public"."jwt_uid"() TO "app_camera";



REVOKE ALL ON FUNCTION "public"."nav_badge_inputs"("p_today" "date", "p_this_month" "date", "p_next_month" "date", "p_open_statuses" "text"[]) FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."nav_badge_inputs"("p_today" "date", "p_this_month" "date", "p_next_month" "date", "p_open_statuses" "text"[]) TO "service_role";



REVOKE ALL ON FUNCTION "public"."publish_roster_month"("p_month" "date", "p_base_version" integer, "p_plan" "jsonb", "p_published_by" integer) FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."publish_roster_month"("p_month" "date", "p_base_version" integer, "p_plan" "jsonb", "p_published_by" integer) TO "service_role";



REVOKE ALL ON FUNCTION "public"."request_leave_withdrawal"("p_request" bigint, "p_reason" "text") FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."request_leave_withdrawal"("p_request" bigint, "p_reason" "text") TO "anon";
GRANT ALL ON FUNCTION "public"."request_leave_withdrawal"("p_request" bigint, "p_reason" "text") TO "authenticated";
GRANT ALL ON FUNCTION "public"."request_leave_withdrawal"("p_request" bigint, "p_reason" "text") TO "service_role";
GRANT ALL ON FUNCTION "public"."request_leave_withdrawal"("p_request" bigint, "p_reason" "text") TO "app_driver";



REVOKE ALL ON FUNCTION "public"."save_roster_month"("p_month" "date", "p_base_version" integer, "p_slots" "jsonb", "p_saved_by" integer, "p_held" "jsonb") FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."save_roster_month"("p_month" "date", "p_base_version" integer, "p_slots" "jsonb", "p_saved_by" integer, "p_held" "jsonb") TO "service_role";



REVOKE ALL ON FUNCTION "public"."schedule_weeks_roster_only"() FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."schedule_weeks_roster_only"() TO "service_role";



GRANT ALL ON FUNCTION "public"."shift_start_context"("p_trip_id" character varying) TO "anon";
GRANT ALL ON FUNCTION "public"."shift_start_context"("p_trip_id" character varying) TO "authenticated";
GRANT ALL ON FUNCTION "public"."shift_start_context"("p_trip_id" character varying) TO "service_role";
GRANT ALL ON FUNCTION "public"."shift_start_context"("p_trip_id" character varying) TO "app_driver";



REVOKE ALL ON FUNCTION "public"."trips_closed_guard"() FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."trips_closed_guard"() TO "service_role";



GRANT ALL ON FUNCTION "public"."trips_count_guard"() TO "anon";
GRANT ALL ON FUNCTION "public"."trips_count_guard"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."trips_count_guard"() TO "service_role";



REVOKE ALL ON FUNCTION "public"."trips_roster_guard"() FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."trips_roster_guard"() TO "service_role";



REVOKE ALL ON FUNCTION "public"."trips_takeover_guard"() FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."trips_takeover_guard"() TO "service_role";



REVOKE ALL ON FUNCTION "public"."users_email_lower"() FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."users_email_lower"() TO "service_role";



REVOKE ALL ON FUNCTION "public"."vehicles_release_guard"() FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."vehicles_release_guard"() TO "service_role";


















GRANT SELECT,INSERT,MAINTAIN ON TABLE "public"."audit_log" TO "service_role";



GRANT ALL ON SEQUENCE "public"."audit_log_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."audit_log_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."audit_log_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."boarding_events" TO "authenticated";
GRANT ALL ON TABLE "public"."boarding_events" TO "service_role";
GRANT SELECT,INSERT ON TABLE "public"."boarding_events" TO "app_camera";



GRANT ALL ON TABLE "public"."boarding_events_ph" TO "anon";
GRANT ALL ON TABLE "public"."boarding_events_ph" TO "authenticated";
GRANT ALL ON TABLE "public"."boarding_events_ph" TO "service_role";



GRANT ALL ON TABLE "public"."bus_checklist" TO "service_role";
GRANT SELECT,INSERT ON TABLE "public"."bus_checklist" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."bus_checklist_checklist_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."bus_checklist_checklist_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."bus_checklist_checklist_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."checklist_items" TO "authenticated";
GRANT ALL ON TABLE "public"."checklist_items" TO "service_role";
GRANT SELECT ON TABLE "public"."checklist_items" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."checklist_items_item_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."checklist_items_item_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."checklist_items_item_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."device_config" TO "service_role";
GRANT SELECT,INSERT,UPDATE ON TABLE "public"."device_config" TO "app_camera";
GRANT SELECT ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("line_ax") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("line_ay") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("line_bx") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("line_by") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("inward_sign") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("use_back_camera") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("wake_requested_at") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("version") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("updated_by") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("updated_at") ON TABLE "public"."device_config" TO "app_driver";



GRANT ALL ON TABLE "public"."device_health_log" TO "anon";
GRANT ALL ON TABLE "public"."device_health_log" TO "authenticated";
GRANT ALL ON TABLE "public"."device_health_log" TO "service_role";
GRANT INSERT ON TABLE "public"."device_health_log" TO "app_camera";



GRANT ALL ON SEQUENCE "public"."device_health_log_log_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."device_health_log_log_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."device_health_log_log_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."device_health_log_ph" TO "anon";
GRANT ALL ON TABLE "public"."device_health_log_ph" TO "authenticated";
GRANT ALL ON TABLE "public"."device_health_log_ph" TO "service_role";



GRANT ALL ON TABLE "public"."device_status" TO "service_role";
GRANT SELECT,INSERT,UPDATE ON TABLE "public"."device_status" TO "app_camera";
GRANT SELECT ON TABLE "public"."device_status" TO "app_driver";



GRANT ALL ON TABLE "public"."driver_availability" TO "service_role";
GRANT SELECT,INSERT ON TABLE "public"."driver_availability" TO "app_driver";



GRANT UPDATE("availability_status") ON TABLE "public"."driver_availability" TO "app_driver";



GRANT UPDATE("updated_at") ON TABLE "public"."driver_availability" TO "app_driver";



GRANT UPDATE("reason") ON TABLE "public"."driver_availability" TO "app_driver";



GRANT ALL ON TABLE "public"."fare_config" TO "service_role";
GRANT SELECT ON TABLE "public"."fare_config" TO "app_driver";



GRANT ALL ON TABLE "public"."inspection_photos" TO "anon";
GRANT ALL ON TABLE "public"."inspection_photos" TO "authenticated";
GRANT ALL ON TABLE "public"."inspection_photos" TO "service_role";



GRANT ALL ON SEQUENCE "public"."inspection_photos_photo_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."inspection_photos_photo_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."inspection_photos_photo_id_seq" TO "service_role";



GRANT SELECT,INSERT,REFERENCES,DELETE,TRIGGER,TRUNCATE,MAINTAIN ON TABLE "public"."leave_requests" TO "anon";
GRANT SELECT,INSERT,REFERENCES,DELETE,TRIGGER,TRUNCATE,MAINTAIN ON TABLE "public"."leave_requests" TO "authenticated";
GRANT ALL ON TABLE "public"."leave_requests" TO "service_role";
GRANT SELECT,INSERT ON TABLE "public"."leave_requests" TO "app_driver";



GRANT UPDATE("status") ON TABLE "public"."leave_requests" TO "app_driver";



GRANT UPDATE("decided_at") ON TABLE "public"."leave_requests" TO "app_driver";



GRANT UPDATE("withdraw_answered_by") ON TABLE "public"."leave_requests" TO "service_role";
GRANT SELECT("withdraw_answered_by") ON TABLE "public"."leave_requests" TO "app_driver";



GRANT UPDATE("withdraw_answer_note") ON TABLE "public"."leave_requests" TO "service_role";
GRANT SELECT("withdraw_answer_note") ON TABLE "public"."leave_requests" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."leave_requests_request_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."leave_requests_request_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."leave_requests_request_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."leave_requests_request_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."maintenance_items" TO "service_role";



GRANT ALL ON SEQUENCE "public"."maintenance_items_item_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."maintenance_items_item_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."maintenance_items_item_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."maintenance_logs" TO "service_role";
GRANT INSERT ON TABLE "public"."maintenance_logs" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."maintenance_logs_log_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."maintenance_logs_log_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."maintenance_logs_log_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."maintenance_notes" TO "service_role";



GRANT ALL ON SEQUENCE "public"."maintenance_notes_note_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."maintenance_notes_note_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."maintenance_notes_note_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."messages" TO "service_role";
GRANT SELECT ON TABLE "public"."messages" TO "app_driver";



GRANT UPDATE("is_read") ON TABLE "public"."messages" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."messages_message_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."messages_message_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."messages_message_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."password_reset_otp" TO "service_role";



GRANT ALL ON SEQUENCE "public"."password_reset_otp_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."password_reset_otp_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."password_reset_otp_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."roles" TO "service_role";



GRANT ALL ON SEQUENCE "public"."roles_role_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."roles_role_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."roles_role_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."roster_gaps" TO "service_role";



GRANT ALL ON TABLE "public"."roster_holds" TO "service_role";



GRANT ALL ON TABLE "public"."roster_months" TO "service_role";
GRANT SELECT ON TABLE "public"."roster_months" TO "app_driver";



GRANT ALL ON TABLE "public"."roster_skips" TO "service_role";



GRANT ALL ON TABLE "public"."roster_slots" TO "service_role";
GRANT SELECT ON TABLE "public"."roster_slots" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."roster_slots_slot_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."roster_slots_slot_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."roster_slots_slot_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."routes" TO "service_role";
GRANT SELECT ON TABLE "public"."routes" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."routes_route_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."routes_route_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."routes_route_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."schedule_weeks" TO "anon";
GRANT ALL ON TABLE "public"."schedule_weeks" TO "authenticated";
GRANT ALL ON TABLE "public"."schedule_weeks" TO "service_role";
GRANT SELECT ON TABLE "public"."schedule_weeks" TO "app_driver";



GRANT ALL ON TABLE "public"."security_incidents" TO "anon";
GRANT ALL ON TABLE "public"."security_incidents" TO "authenticated";
GRANT ALL ON TABLE "public"."security_incidents" TO "service_role";



GRANT ALL ON SEQUENCE "public"."security_incidents_incident_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."security_incidents_incident_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."security_incidents_incident_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."telemetry_data" TO "service_role";
GRANT INSERT ON TABLE "public"."telemetry_data" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."telemetry_data_telemetry_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."telemetry_data_telemetry_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."telemetry_data_telemetry_id_seq" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."trip_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."trip_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."trip_id_seq" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."trip_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."trip_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."trip_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."trips" TO "service_role";
GRANT SELECT ON TABLE "public"."trips" TO "app_driver";
GRANT SELECT ON TABLE "public"."trips" TO "app_camera";



GRANT UPDATE("trip_status") ON TABLE "public"."trips" TO "app_driver";



GRANT UPDATE("estimated_revenue") ON TABLE "public"."trips" TO "app_driver";



GRANT UPDATE("total_boarded") ON TABLE "public"."trips" TO "app_driver";
GRANT UPDATE("total_boarded") ON TABLE "public"."trips" TO "app_camera";



GRANT UPDATE("actual_start_time") ON TABLE "public"."trips" TO "app_driver";



GRANT UPDATE("actual_end_time") ON TABLE "public"."trips" TO "app_driver";



GRANT UPDATE("count_heartbeat") ON TABLE "public"."trips" TO "app_camera";



GRANT UPDATE("counter_device_id") ON TABLE "public"."trips" TO "app_camera";



GRANT ALL ON TABLE "public"."users" TO "service_role";
GRANT SELECT ON TABLE "public"."users" TO "anon";



GRANT ALL ON TABLE "public"."users_app" TO "service_role";
GRANT SELECT ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("contact_number") ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("address") ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("emergency_contact_name") ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("emergency_contact_number") ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("updated_at") ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("last_login") ON TABLE "public"."users_app" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."users_user_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."users_user_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."users_user_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."vehicles" TO "service_role";
GRANT SELECT ON TABLE "public"."vehicles" TO "app_driver";
GRANT SELECT ON TABLE "public"."vehicles" TO "app_camera";



GRANT UPDATE("vehicle_status") ON TABLE "public"."vehicles" TO "app_driver";



GRANT UPDATE("updated_at") ON TABLE "public"."vehicles" TO "app_driver";



GRANT UPDATE("counter_device_id") ON TABLE "public"."vehicles" TO "app_camera";









ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "service_role";






ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "service_role";






ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "service_role";































