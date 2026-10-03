-- Keeps every email address in lower case, so an address signs in whatever case it is
-- typed in, as mail providers treat it.
--
-- users.email_address  Existing addresses are trimmed and lowered once. A trigger lowers
--                      every address written from now on, whether it comes from the
--                      dashboard, an edge function or the SQL editor.
--
-- Nothing changes if two accounts hold the same address in different cases, because
-- lowering both would break the column's unique rule. The script stops and names them;
-- rename or remove one, then run it again.
--
-- Order: apply this, then release the dashboard and deploy auth-login,
-- password-reset-request and password-reset-verify straight after. In between, an account
-- whose address had capitals signs in only when the address is typed in lower case.

begin;

do $check$
declare
  v_clash text;
begin
  select string_agg(addr, ', ' order by addr)
    into v_clash
    from (select lower(btrim(email_address)) as addr
            from public.users
           group by 1
          having count(*) > 1) c;

  if v_clash is not null then
    raise exception 'more than one account holds each of these addresses in a different case: %', v_clash;
  end if;
end
$check$;

create or replace function public.users_email_lower()
returns trigger
language plpgsql
set search_path to 'public'
as $$
begin
  new.email_address := lower(btrim(new.email_address));
  return new;
end;
$$;

revoke all on function public.users_email_lower() from public, anon, authenticated;

drop trigger if exists trg_users_email_lower on public.users;
create trigger trg_users_email_lower
  before insert or update of email_address on public.users
  for each row execute function public.users_email_lower();

update public.users
   set email_address = lower(btrim(email_address))
 where email_address is distinct from lower(btrim(email_address));

commit;
