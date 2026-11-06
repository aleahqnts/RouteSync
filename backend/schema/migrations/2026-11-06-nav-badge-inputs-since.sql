-- Adds nav_badge_inputs_since: the sidebar badge rows, sent only when they changed.
--
-- Every open dashboard page asks for the badge counts every five seconds, and a hidden
-- one every thirty. nav_badge_inputs answers each time with every row the counts are
-- worked out from, though on most askings none of them has changed since the last.
--
-- This works out the same rows and a fingerprint of them. When the caller already holds
-- rows with that fingerprint the answer is the fingerprint alone, and the caller counts
-- again from what it holds, against the current time. The rows go out again only when
-- one of them differs.
--
-- A fingerprint that changes when no row did, such as rows coming back in another order,
-- only sends the rows again. It can never hide a change.

begin;

create or replace function public.nav_badge_inputs_since(
  p_today date,
  p_this_month date,
  p_next_month date,
  p_open_statuses text[],
  p_sections text default 'all',
  p_known text default null)
returns json
language plpgsql
stable
set search_path to 'public'
as $$
declare
  v_inputs json := nav_badge_inputs(p_today, p_this_month, p_next_month, p_open_statuses, p_sections);
  v_hash   text := md5(v_inputs::text);
begin
  if v_hash = p_known then
    return json_build_object('hash', v_hash, 'inputs', null);
  end if;
  return json_build_object('hash', v_hash, 'inputs', v_inputs);
end
$$;

comment on function public.nav_badge_inputs_since(date, date, date, text[], text, text) is
  'nav_badge_inputs with a fingerprint. Returns {hash, inputs}; inputs is null when p_known already equals hash, so a caller holding those rows is not sent them again.';

revoke all on function public.nav_badge_inputs_since(date, date, date, text[], text, text) from public, anon, authenticated;
grant execute on function public.nav_badge_inputs_since(date, date, date, text[], text, text) to service_role;

commit;

notify pgrst, 'reload schema';
