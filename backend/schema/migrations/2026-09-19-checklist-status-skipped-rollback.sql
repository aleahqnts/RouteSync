-- Backs out 2026-09-19-checklist-status-skipped.sql.
--
-- Postgres cannot drop an enum label. There is no alter type ... drop value and there
-- never has been, because stored rows and index entries hold the label by an internal
-- identifier that a drop would leave pointing at nothing. The only way to remove a label
-- is to replace the whole type.
--
-- The procedure
--
-- A second type is created carrying the labels that should remain, the column is moved
-- onto it with a cast through text, the old type is dropped, and the new one takes its
-- name. Every row in bus_checklist is rewritten, so the table is locked for the duration
-- and the cost grows with its size.
--
-- The column default has to come off first. bus_checklist.checklist_status defaults to
-- Pending cast to the old type, and a default written against a type cannot survive that
-- type being replaced underneath it. It is dropped before the conversion and written
-- again afterwards against the type under its restored name. Doing only the first half
-- leaves the column with no default, and the next insert that omits the status then fails
-- on the not null constraint instead.
--
-- Anything else standing on the type has to be rebuilt as well. A view that selects the
-- column, a function declaring a parameter or variable of the type, a check constraint or
-- index naming it, another table's column using it: each is a dependency that Postgres
-- will refuse to drop the type through, and each has to be taken down before the
-- conversion and put back after. The column default is the only such dependency in the
-- schema as it stands, but that is a fact about today's schema rather than a guarantee.
-- Check before running this.
--
-- This is why removing an enum label is not a routine operation. Adding one is a single
-- statement that cannot lose anything. Taking one away rewrites a table, drops and
-- rebuilds every object resting on the type, and destroys the distinction the label was
-- there to record. Leaving an unused label in place is almost always the better answer.
--
-- The refusal
--
-- The script stops before changing anything if any bus_checklist row still reads Skipped,
-- and says how many. Converting with those rows present would either fail on the cast or
-- force them onto some other label, and either way the fact that those trips had no
-- inspection would be gone with no way back. Settle those rows first by deciding what
-- each should say instead, then run this.
--
-- Unlike the migration, this runs in a transaction and is entitled to. The restriction is
-- on using a label added by alter type, not on one belonging to a type created outright,
-- and a type created by create type is usable straight away. So the whole replacement
-- either lands or leaves nothing touched.
--
-- Running this against a database that never carried the label rebuilds the type to the
-- shape it already had. Harmless, and pointless.

begin;

-- Refuse rather than destroy. The comparison goes through text so this still runs on a
-- database where the label was never added, where comparing the column to Skipped
-- directly would error on an unknown label instead of reporting zero.
do $guard$
declare
  v_n integer;
begin
  select count(*) into v_n
    from public.bus_checklist
   where checklist_status::text = 'Skipped';

  if v_n > 0 then
    raise exception 'refused: Skipped is still in use on % bus_checklist row(s). Dropping the label would destroy what those rows record. Decide what each should say instead, change them, then run this again.', v_n;
  end if;
end
$guard$;

-- A new type carrying the old set of labels, in their original order.
create type public.checklist_status_enum_new as enum (
  'Passed',
  'Failed',
  'Pending',
  'Passed with Defects'
);

-- The default is written against the type being replaced and cannot outlive it.
alter table public.bus_checklist
  alter column checklist_status drop default;

-- Through text, because there is no cast between two enum types.
alter table public.bus_checklist
  alter column checklist_status type public.checklist_status_enum_new
  using checklist_status::text::public.checklist_status_enum_new;

-- Nothing refers to the old type now, so it goes without cascade.
drop type public.checklist_status_enum;

alter type public.checklist_status_enum_new rename to checklist_status_enum;

-- The default, restored against the type under its old name.
alter table public.bus_checklist
  alter column checklist_status set default 'Pending'::public.checklist_status_enum;

-- The documentation added with the label goes with it. The type comment leaves with the
-- type it was attached to, but the column comment survives the conversion and has to be
-- cleared by hand, or the column keeps describing a label it no longer has.
comment on column public.bus_checklist.checklist_status is null;

commit;
