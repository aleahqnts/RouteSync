-- Backs out 2026-09-23-inspection-photos.sql.
--
-- Safe while the driver app has no photograph capture, which is the window this exists
-- for. Once drivers are uploading, running this drops the record of every photograph
-- taken, and the objects themselves survive in the bucket with nothing pointing at them.
--
-- The bucket is left in place deliberately. Dropping a bucket that still holds objects
-- fails, and dropping one that does not is a decision about stored evidence rather than
-- about schema. Empty and remove it by hand if that is what is wanted.
--
-- The grant on storage.objects is left alone as well. It is not known here whether
-- app_driver held it beforehand for some other bucket, and revoking a privilege this
-- migration may not have granted would break something it never touched.

begin;

drop policy if exists p_inspection_photo_driver_delete on storage.objects;
drop policy if exists p_inspection_photo_driver_update on storage.objects;
drop policy if exists p_inspection_photo_driver_insert on storage.objects;
drop policy if exists p_inspection_photo_driver_select on storage.objects;

drop table if exists public.inspection_photos;

commit;
