-- Backs out 2026-09-19-trip-takeover.sql.
--
-- What returns to being possible
--
-- A bus can be on two active trips again. A driver who forgets to press End leaves their
-- trip running, the next driver's start is accepted without a question being asked, and the
-- bus holds both trips until the stale trip closer reaches the first of them a full day past
-- its scheduled end.
--
-- The counter phone bound to that bus goes back to polling for the active trip and taking
-- whichever row the answer happens to put first, with no ordering behind that choice. So it
-- can count an afternoon's passengers into the morning's trip, or into a trip that is closed
-- underneath it, in which case they are counted nowhere at all.
--
-- The refusal goes too, which is the part a driver notices. A start that was refused because
-- a colleague was still driving is simply accepted, and the colleague's trip stays open
-- alongside it rather than being ended for them. Nothing else in the system refuses it.
--
-- What is kept
--
-- Nothing is destroyed. Trips closed at handover keep their Completed status and the end
-- time they were stamped with, and the trip_taken_over rows already in audit_log stay, since
-- that table does not permit deletion. What is lost is the rule, not the record.
--
-- trg_trips_count_guard and trg_trips_roster_edit are untouched and go on working.

begin;

drop trigger if exists trg_trips_takeover_guard on public.trips;

drop function if exists public.trips_takeover_guard();

commit;
