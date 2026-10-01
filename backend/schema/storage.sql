-- Storage: the buckets the apps upload to, and who may read and write them.
--
-- The schema dump leaves Supabase's storage schema out, since the platform owns its
-- tables. The buckets and the policies on them are this project's own, though, and a
-- database rebuilt from schema.sql alone would have neither. This is them. Update this
-- file in the same change that adds or alters a bucket or a storage policy.
--
--   camera-snapshots   calibration frames from the counter phone
--   inspection-photos  photos a driver attaches to a failed checklist item
--
-- Run after roles.sql and schema.sql. Each statement can be run again safely.

begin;

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('camera-snapshots', 'camera-snapshots', false, 2097152, '{image/jpeg}'::text[])
on conflict (id) do nothing;

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('inspection-photos', 'inspection-photos', false, 524288, '{image/jpeg}'::text[])
on conflict (id) do nothing;

grant select on storage.buckets to app_camera;
grant delete, insert, select, update on storage.objects to app_camera;
grant select on storage.buckets to app_driver;
grant delete, insert, select, update on storage.objects to app_driver;

drop policy if exists p_snap_bucket_read on storage.buckets;
create policy p_snap_bucket_read on storage.buckets as permissive for select to app_camera, app_driver
  using ((id = 'camera-snapshots'::text));

drop policy if exists p_inspection_photo_driver_delete on storage.objects;
create policy p_inspection_photo_driver_delete on storage.objects as permissive for delete to app_driver
  using (((bucket_id = 'inspection-photos'::text) AND ((storage.foldername(name))[1] = (jwt_uid())::text)));

drop policy if exists p_inspection_photo_driver_insert on storage.objects;
create policy p_inspection_photo_driver_insert on storage.objects as permissive for insert to app_driver
  with check (((bucket_id = 'inspection-photos'::text) AND ((storage.foldername(name))[1] = (jwt_uid())::text)));

drop policy if exists p_inspection_photo_driver_select on storage.objects;
create policy p_inspection_photo_driver_select on storage.objects as permissive for select to app_driver
  using (((bucket_id = 'inspection-photos'::text) AND ((storage.foldername(name))[1] = (jwt_uid())::text)));

drop policy if exists p_inspection_photo_driver_update on storage.objects;
create policy p_inspection_photo_driver_update on storage.objects as permissive for update to app_driver
  using (((bucket_id = 'inspection-photos'::text) AND ((storage.foldername(name))[1] = (jwt_uid())::text)))
  with check (((bucket_id = 'inspection-photos'::text) AND ((storage.foldername(name))[1] = (jwt_uid())::text)));

drop policy if exists p_snap_camera_all on storage.objects;
create policy p_snap_camera_all on storage.objects as permissive for all to app_camera
  using (((bucket_id = 'camera-snapshots'::text) AND (name = (jwt_dev() || '.jpg'::text))))
  with check (((bucket_id = 'camera-snapshots'::text) AND (name = (jwt_dev() || '.jpg'::text))));

drop policy if exists p_snap_driver_read on storage.objects;
create policy p_snap_driver_read on storage.objects as permissive for select to app_driver
  using (((bucket_id = 'camera-snapshots'::text) AND (name = (driver_active_camera() || '.jpg'::text))));

commit;
