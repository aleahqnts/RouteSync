-- Removes the two columns left behind by the count-split model.
--
-- Run only once every phone in the fleet is on a build that neither reads
-- boarded_counted nor writes boarded_adjustment. Until then the guard mirrors and
-- clears them, which is what keeps an older build agreeing with the current rule.
--
-- A build that still reads boarded_counted will get 400 from PostgREST once these are
-- gone, and on the counter phone that means its post-trip reconcile throws on every
-- attempt and the count is held rather than delivered. It is not lost, but it does not
-- arrive until that phone is updated.
--
-- Nothing here changes what any trip reports. total_boarded has been the whole answer
-- since the guard was replaced.

begin;

alter table public.trips
  drop column if exists boarded_counted,
  drop column if exists boarded_adjustment;

commit;

-- The guard still assigns both columns, so it has to be replaced in the same sitting.
-- Re-run 2026-09-07-driver-may-lower-while-camera-is-dark.sql with the two
-- NEW.boarded_* assignments and the trailing UPDATE removed.
