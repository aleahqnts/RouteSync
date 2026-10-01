-- Removes three database objects nothing uses any more.
--
-- routes_before_osm         The route rows as they stood before the real BGC routes
--                           replaced them on 2026-09-26, kept for that change's rollback.
--                           That change shipped in 1.1.0 and its rollback left the
--                           repository with it, so nothing reads this table.
-- security_detector_state   One row saying how far the security incident detector had read
--                           the audit log. The dashboard now holds that in memory and, after
--                           a restart, reads the last two days again; incidents are written
--                           so that reading a span twice changes nothing.
-- whoami()                  A diagnostic that returned the caller's role and token claims.
--                           No app, policy, trigger or function calls it.
--
-- Order: release the dashboard that no longer reads security_detector_state first. An older
-- dashboard finds the table gone and its incident scans stop until it is updated; nothing
-- else is affected.
--
-- Export routes_before_osm first if its rows might ever be wanted: the rollback recreates
-- the table empty.

begin;

drop table if exists public.routes_before_osm;
drop table if exists public.security_detector_state;
drop function if exists public.whoami();

commit;

notify pgrst, 'reload schema';
