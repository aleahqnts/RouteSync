-- Backs out 2026-10-01-drop-unused.sql.
--
-- Recreates the three objects as they were, without routes_before_osm's rows, which this
-- cannot bring back. security_detector_state starts at now, which only a dashboard older
-- than 1.3.1 reads.

begin;

create table if not exists public.routes_before_osm (
    route_id integer not null,
    route_name character varying(100) not null,
    origin character varying(100) not null,
    destination character varying(100) not null,
    waypoints_json text,
    stops_json text,
    updated_at timestamp with time zone,
    saved_at timestamp with time zone default now() not null,
    constraint pk_routes_before_osm primary key (route_id)
);
alter table public.routes_before_osm enable row level security;

create table if not exists public.security_detector_state (
    id smallint default 1 not null,
    scanned_through timestamp with time zone not null,
    constraint ck_security_detector_state_single check (id = 1),
    constraint pk_security_detector_state primary key (id)
);
alter table public.security_detector_state enable row level security;
insert into public.security_detector_state (id, scanned_through) values (1, now())
on conflict (id) do nothing;
grant all on table public.security_detector_state to service_role;

create or replace function public.whoami() returns json
    language sql stable
    as $$
  select json_build_object(
    'current_user', current_user,
    'claims', nullif(current_setting('request.jwt.claims', true), '')::json
  );
$$;

commit;

notify pgrst, 'reload schema';
