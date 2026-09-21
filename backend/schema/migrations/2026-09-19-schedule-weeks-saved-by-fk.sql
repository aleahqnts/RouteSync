-- Ties schedule_weeks.saved_by to the operator it names.
--
-- Background
--
-- schedule_weeks records which week the planner has saved and who saved it. saved_by
-- holds a user id, but nothing has ever required that user to exist. roster_months holds
-- the same kind of reference and does constrain it, so the two tables state the same
-- relationship with different force, and schedule_weeks is the only table in the schema
-- carrying a user id with nothing behind it.
--
-- The practical cost is that a deleted operator leaves a week attributed to a number,
-- and nothing reports the inconsistency because nothing is checking.
--
-- Safety
--
-- The constraint is refused if any week already names a user that has gone. That is
-- deliberate: nulling those rows would quietly erase who saved a week, and choosing to
-- do so is not this migration's decision to make. The check below names them first so
-- the choice can be made deliberately.

begin;

do $guard$
declare
  v_orphans integer;
begin
  -- Postgres has no "add constraint if not exists", and running a migration twice is an
  -- ordinary thing to do. Leaving early keeps the second run quiet instead of failing on
  -- a constraint the first run already added.
  if exists (
    select 1 from pg_constraint
     where conrelid = 'public.schedule_weeks'::regclass
       and conname  = 'fk_schedule_weeks_saved_by'
  ) then
    raise notice 'fk_schedule_weeks_saved_by is already in place';
    return;
  end if;

  select count(*) into v_orphans
    from public.schedule_weeks w
   where w.saved_by is not null
     and not exists (select 1 from public.users u where u.user_id = w.saved_by);

  if v_orphans > 0 then
    raise exception
      'schedule_weeks has % week(s) naming a user that no longer exists. Decide what '
      'should happen to them before adding the constraint: '
      'select week_start, saved_by from public.schedule_weeks w where saved_by is not null '
      'and not exists (select 1 from public.users u where u.user_id = w.saved_by);',
      v_orphans;
  end if;

  execute 'alter table public.schedule_weeks
             add constraint fk_schedule_weeks_saved_by
             foreign key (saved_by) references public.users (user_id)';
end
$guard$;

comment on column public.schedule_weeks.saved_by is 'The operator who saved this week. Null for a week written by a roster publish rather than by hand.';

commit;
