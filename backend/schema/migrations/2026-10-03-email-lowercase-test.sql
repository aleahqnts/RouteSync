-- Checks 2026-10-03-email-lowercase.sql. Run it after the migration, in the Supabase SQL
-- editor. A failure raises; a pass prints one notice. The trigger function is tried on a
-- temporary table, so no account, ID or audit entry is written.

do $test$
declare
  fn constant text := 'public.users_email_lower()';
  v_left integer;
  v_addr text;
begin
  select count(*) into v_left
    from public.users
   where email_address is distinct from lower(btrim(email_address));
  if v_left > 0 then
    raise exception '% addresses still hold capitals or spaces', v_left;
  end if;

  if not exists (select 1 from pg_trigger
                  where tgname = 'trg_users_email_lower'
                    and tgrelid = 'public.users'::regclass
                    and not tgisinternal) then
    raise exception 'the trigger on users is missing';
  end if;

  if has_function_privilege('anon', fn, 'execute') or has_function_privilege('authenticated', fn, 'execute') then
    raise exception 'anon or authenticated can call users_email_lower';
  end if;

  -- A new address with capitals and spaces is stored lowered, and so is an edit.
  create temporary table email_case_trial (email_address varchar(100)) on commit drop;
  create trigger trial_email_lower before insert or update of email_address on email_case_trial
    for each row execute function public.users_email_lower();

  insert into email_case_trial values ('  Case.CHECK@Example.TEST ') returning email_address into v_addr;
  if v_addr <> 'case.check@example.test' then
    raise exception 'insert stored %', v_addr;
  end if;

  update email_case_trial set email_address = 'Case.Again@Example.TEST' returning email_address into v_addr;
  if v_addr <> 'case.again@example.test' then
    raise exception 'update stored %', v_addr;
  end if;

  drop table email_case_trial;
  raise notice 'email-lowercase: all checks passed';
end
$test$;
