-- Links a maintenance line back to the inspection item that raised it.
--
-- A fault reported twice should reopen the line it already has rather than start a
-- second one. Deciding that meant comparing the line's label against the checklist
-- item's, which holds only while nobody edits either. Rename an item and the returning
-- fault matches nothing, so a second line opens for a fault that already had one: a
-- mechanic closes one, the other keeps the bus flagged, and nothing anywhere reports an
-- error. Two items sharing a label fail the other way round, with one fault's line
-- quietly answering for both.
--
-- The label stays, and stays authoritative for what the line says. It is what somebody
-- read on the day, and an order describes faults people actually saw. The id is what
-- survives the wording changing.

begin;

-- Nullable on purpose, and it will stay that way. Lines raised before this existed have
-- no id to give, and a line an admin types by hand describes something no inspection
-- item covers. Both keep matching by label, which is all they ever had.
alter table public.maintenance_items
  add column if not exists checklist_item_id bigint;

alter table public.maintenance_items
  drop constraint if exists fk_maintenance_items_checklist_item;

alter table public.maintenance_items
  add constraint fk_maintenance_items_checklist_item
    foreign key (checklist_item_id) references public.checklist_items (item_id);

comment on column public.maintenance_items.checklist_item_id is
  'The inspection item this line was raised by, when one was. Null for a line typed by hand and for every line raised before it was recorded, both of which are matched by label instead.';

-- Matching a returning fault reads every line on one order and looks for one id.
create index if not exists ix_maintenance_items_log_checklist_item
  on public.maintenance_items (log_id, checklist_item_id);

commit;
