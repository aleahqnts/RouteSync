-- Checks that checklist_status_enum carries the Skipped label.
--
-- The check is a catalogue lookup rather than an insert, and that is forced rather than
-- chosen. Postgres will not let a new enum label be used by the transaction that added
-- it, so a test that tried to insert a bus_checklist row reading Skipped alongside the
-- migration would fail on the insert even though the migration itself had worked. Reading
-- pg_enum asks the same question without meeting that rule, and it answers the same way
-- whether the migration ran a second ago or a month ago.
--
-- Read only. Nothing is inserted, updated or deleted, so there is no transaction to open
-- and nothing is left behind. Run it in the Supabase SQL editor after the migration. A
-- missing label raises and aborts; a pass prints one notice naming every label found, so
-- a run also serves as a record of the label set at that moment.
--
-- It checks the label and nothing else. Whether the start gate refuses to treat a skip as
-- a pass, and whether a skip leaves vehicles.out_of_service alone, are decisions made in
-- the driver app and the inspection endpoint, and they have to be checked there.

do $test$
declare
  v_present boolean;
  v_labels  text;
begin
  select exists (
           select 1
             from pg_catalog.pg_enum e
             join pg_catalog.pg_type t on t.oid = e.enumtypid
             join pg_catalog.pg_namespace n on n.oid = t.typnamespace
            where n.nspname = 'public'
              and t.typname = 'checklist_status_enum'
              and e.enumlabel = 'Skipped'
         )
    into v_present;

  -- Gathered either way, so the failure says what the type does carry instead of only
  -- what it lacks.
  select string_agg(e.enumlabel, ', ' order by e.enumsortorder)
    into v_labels
    from pg_catalog.pg_enum e
    join pg_catalog.pg_type t on t.oid = e.enumtypid
    join pg_catalog.pg_namespace n on n.oid = t.typnamespace
   where n.nspname = 'public'
     and t.typname = 'checklist_status_enum';

  if not v_present then
    raise exception 'fail: checklist_status_enum has no Skipped label (it carries: %)',
      coalesce(v_labels, 'nothing, the type itself was not found');
  end if;

  raise notice 'pass: checklist_status_enum carries Skipped (labels: %)', v_labels;
end
$test$;
