-- Backs out 2026-09-19-boarding-events.sql.
--
-- Deploy a counter phone that does not write events first. A phone still sending them
-- gets 404 from PostgREST on every attempt, and because its events are held on the
-- device until the write is accepted, its queue then grows without bound.
--
-- Destructive: every crossing recorded so far is dropped with the table. Nothing else
-- depends on them, since the reported count has always lived in trips.total_boarded, so
-- no figure anywhere changes. What is lost is the evidence behind those figures, which
-- cannot be reconstructed from anything that remains.

begin;

drop view if exists public.boarding_events_ph;

-- The policy and the grant belong to the table and go with it. Naming them separately
-- would fail here rather than tidy up, since both statements require the table to exist.
drop table if exists public.boarding_events;

commit;
