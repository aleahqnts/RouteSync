-- Unusual activity in the audit trail, grouped into incidents.
--
-- Background
--
-- The audit trail records every sign-in, every refusal and every page a user was turned
-- away from, and it is read one entry at a time. Five failed sign-ins from one address
-- are five separate lines among hundreds, and nothing on the page says they belong
-- together. An incident is those lines gathered under one row, with a reason.
--
-- Detection only. Nothing here blocks anybody. Drivers sign in on mobile data behind
-- carrier address sharing, so a block keyed on an address would lock out every driver
-- sharing it because one of them mistyped a password. Both sign-in paths already slow
-- down repeated failures per account, which is the right key for that network, and this
-- exists to see what that throttle cannot.
--
-- Model
--
-- An incident does not hold its entries. It holds the filter that found them: which
-- actions, which column, which value, over which span. The audit trail refuses every
-- update and delete, so the same filter returns the same entries for as long as the table
-- exists, and nothing has to be kept in step. The filter is stored on the row rather than
-- looked up from the rule that raised it, so widening a rule later cannot change which
-- entries an old incident shows.
--
-- The span only grows. The first entry is fixed when the incident is raised; later
-- entries for the same key extend the last one until the activity goes quiet.
--
-- Review
--
-- An incident needs review until somebody reviews it, and again whenever it gains entries
-- that person did not see. The count seen at review is kept for that reason rather than
-- the time of the review. Comparing times would miss an entry that happened before the
-- review but was only gathered after it, which is exactly an entry the reviewer never saw.

begin;

create table if not exists public.security_incidents (
  incident_id          bigint generated always as identity,
  rule                 text        not null,
  severity             text        not null,
  key_column           text        not null,
  key_value            text        not null,
  actions              text[]      not null,
  first_seen_at        timestamptz not null,
  last_seen_at         timestamptz not null,
  event_count          integer     not null,
  detected_at          timestamptz not null default now(),
  reviewed_at          timestamptz,
  reviewed_by          text,
  review_note          text,
  reviewed_event_count integer,
  needs_review         boolean generated always as
                         (reviewed_at is null or event_count > reviewed_event_count) stored,

  constraint pk_security_incidents primary key (incident_id),

  -- One incident per burst. A scan that finds the same burst twice, whether after a
  -- restart or because two scans overlapped, collides here instead of raising a copy.
  constraint uq_security_incidents_episode unique (rule, key_value, first_seen_at),

  constraint ck_security_incidents_severity
    check (severity in ('low', 'medium', 'high')),

  -- The columns a filter may name. Anything else would be a way to point an incident at
  -- an arbitrary column of the audit trail.
  constraint ck_security_incidents_key
    check (key_column in ('ip', 'target_id', 'actor_id')),

  constraint ck_security_incidents_span
    check (last_seen_at >= first_seen_at),

  -- A review is all three or none of them.
  constraint ck_security_incidents_review
    check ((reviewed_at is null) = (reviewed_by is null)
       and (reviewed_at is null) = (reviewed_event_count is null))
);

comment on table public.security_incidents is 'A burst of unusual activity in audit_log, gathered under one row. Holds the filter that finds its entries, never the entries themselves.';

comment on column public.security_incidents.rule is 'Which rule raised it, by a stable code. The wording shown for a rule lives in the dashboard and may change; this does not.';

comment on column public.security_incidents.key_column is 'The audit_log column the incident is about: ip for an address, target_id for the account acted on, actor_id for the account acting.';

comment on column public.security_incidents.actions is 'The audit_log actions the incident gathers. Frozen when it is raised, so a later change to the rule cannot change which entries it shows.';

comment on column public.security_incidents.first_seen_at is 'The first entry that counted toward crossing the threshold, not the entry that crossed it. Fixed once raised.';

comment on column public.security_incidents.last_seen_at is 'The latest entry gathered. Extended while activity continues.';

comment on column public.security_incidents.event_count is 'Entries matching the filter over the span, worked out from audit_log rather than counted up, so seeing an entry twice changes nothing.';

comment on column public.security_incidents.reviewed_event_count is 'How many entries the reviewer was shown. More than this means activity they have not seen.';

comment on column public.security_incidents.needs_review is 'Unreviewed, or grown since review. Stored because the rail counts it, and a filter cannot compare two columns.';

-- The incidents page and the rail both ask for what needs review, newest activity first.
create index if not exists ix_security_incidents_review
  on public.security_incidents (needs_review, last_seen_at desc);

-- The detector asks for the most recent incident for a rule and key before deciding
-- whether new activity extends it or starts another.
create index if not exists ix_security_incidents_key
  on public.security_incidents (rule, key_value, last_seen_at desc);

-- ---------------------------------------------------------------------------
-- Where the detector got to
-- ---------------------------------------------------------------------------

-- One row. The detector runs in the dashboard's host, which sleeps when nobody is using
-- it, while the edge functions go on writing to the trail. Starting each scan from where
-- the last one reached, rather than from a fixed distance back, means a long sleep delays
-- an incident instead of losing it.
create table if not exists public.security_detector_state (
  id              smallint    not null default 1,
  scanned_through timestamptz not null,

  constraint pk_security_detector_state primary key (id),
  constraint ck_security_detector_state_single check (id = 1)
);

comment on table public.security_detector_state is 'How far the incident detector has read. One row.';

-- A week back, so the first scan surfaces recent activity rather than starting blind.
insert into public.security_detector_state (id, scanned_through)
values (1, now() - interval '7 days')
on conflict (id) do nothing;

-- ---------------------------------------------------------------------------
-- Access
-- ---------------------------------------------------------------------------

-- Row level security with no policy for any device role. The dashboard reads and writes
-- both tables with the service key, which row level security does not apply to, and no
-- phone has any business with either.
alter table public.security_incidents enable row level security;
alter table public.security_detector_state enable row level security;

commit;
