-- Undoes 2026-11-18-profile-photos.sql. Photographs already in the bucket are left there;
-- empty the bucket from the Storage page first if it should go as well.

begin;

drop policy if exists p_profile_photo_driver_delete on storage.objects;
drop policy if exists p_profile_photo_driver_insert on storage.objects;
drop policy if exists p_profile_photo_driver_select on storage.objects;

-- The view has to lose the column before the table can.
drop view public.users_app;
create view public.users_app as
 select user_id,
    first_name,
    middle_name,
    last_name,
    email_address,
    role_id,
    account_status,
    contact_number,
    address,
    emergency_contact_name,
    emergency_contact_number,
    created_at,
    updated_at,
    last_login
   from public.users
  where (user_id = coalesce(public.jwt_uid(), user_id))
  with cascaded check option;

grant all on public.users_app to service_role;
grant select on public.users_app to app_driver;
grant update (contact_number, address, emergency_contact_name, emergency_contact_number, updated_at, last_login)
  on public.users_app to app_driver;

alter table public.users drop constraint if exists ck_users_photo_key;
alter table public.users drop column if exists photo_key;

commit;
