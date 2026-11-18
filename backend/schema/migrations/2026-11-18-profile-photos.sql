-- Profile photographs for every account, set by the person themselves from the driver app
-- or the dashboard profile, and removable by an administrator from the Users page.
--
-- The photograph lives in the private profile-photos bucket under the owner's own folder,
-- named <user_id>/<32 hex>.jpg. A change uploads under a new name and then points the row
-- at it, so a name is never reused and a reader may cache one for good.

begin;

alter table public.users add column if not exists photo_key text;

comment on column public.users.photo_key is
  'Path of the profile photograph in the profile-photos bucket, or null for none. Always inside the owner''s own folder, which the check constraint holds the driver app to.';

-- The driver app writes this column itself, so the row has to refuse a name outside the
-- owner's folder. Without it a driver could point their row at someone else's photograph.
alter table public.users drop constraint if exists ck_users_photo_key;
alter table public.users add constraint ck_users_photo_key
  check (photo_key is null or photo_key ~ ('^' || user_id::text || '/[0-9a-f]{32}\.jpg$'));

create or replace view public.users_app as
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
    last_login,
    photo_key
   from public.users
  where (user_id = coalesce(public.jwt_uid(), user_id))
  with cascaded check option;

grant update (photo_key) on public.users_app to app_driver;

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('profile-photos', 'profile-photos', false, 262144, '{image/jpeg}'::text[])
on conflict (id) do nothing;

drop policy if exists p_profile_photo_driver_delete on storage.objects;
create policy p_profile_photo_driver_delete on storage.objects as permissive for delete to app_driver
  using (((bucket_id = 'profile-photos'::text) AND ((storage.foldername(name))[1] = (jwt_uid())::text)));

drop policy if exists p_profile_photo_driver_insert on storage.objects;
create policy p_profile_photo_driver_insert on storage.objects as permissive for insert to app_driver
  with check (((bucket_id = 'profile-photos'::text) AND ((storage.foldername(name))[1] = (jwt_uid())::text)));

drop policy if exists p_profile_photo_driver_select on storage.objects;
create policy p_profile_photo_driver_select on storage.objects as permissive for select to app_driver
  using (((bucket_id = 'profile-photos'::text) AND ((storage.foldername(name))[1] = (jwt_uid())::text)));

commit;
