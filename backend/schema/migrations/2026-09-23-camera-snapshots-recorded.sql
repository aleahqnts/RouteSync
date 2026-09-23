-- Records the camera-snapshots bucket and its policies, which until now existed only in
-- the Supabase dashboard.
--
-- Nothing here is a change. The bucket was created by hand when remote camera control was
-- built, its policies were written in the dashboard editor, and no file in this repository
-- mentioned either. A production dependency that cannot be rebuilt from source is a
-- dependency nobody can reason about, and the gap surfaced while adding a second bucket
-- alongside it.
--
-- Transcribed from pg_policies and storage.buckets on 2026-09-23. Running it against a
-- database that already holds them changes nothing; running it against an empty one
-- rebuilds what production has.
--
-- What the policies say
--
-- A counter phone reaches exactly one object, named after itself, and may do anything to
-- it: it writes the snapshot on a wake request and deletes it once the calibration is
-- applied, cancelled or timed out.
--
-- A driver reads the snapshot belonging to the camera on the bus they are currently
-- driving, and nothing else. driver_active_camera resolves that from their own active trip
-- rather than from anything the request carries, so a driver between trips reaches no
-- object at all.
--
-- Both are exact name matches rather than folder prefixes, which is available here because
-- each device owns a single object. The inspection-photos bucket cannot follow the same
-- pattern: a driver accumulates photographs, so ownership is expressed by folder.

begin;

-- 2 MB against a doorway photograph, which is larger than inspection photographs need
-- because a calibration line is drawn on top of this one and detail matters.
insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('camera-snapshots', 'camera-snapshots', false, 2097152, array['image/jpeg'])
on conflict (id) do nothing;

drop policy if exists p_snap_camera_all on storage.objects;
create policy p_snap_camera_all on storage.objects
  for all to app_camera
  using (
    bucket_id = 'camera-snapshots'
    and name = (public.jwt_dev() || '.jpg')
  )
  with check (
    bucket_id = 'camera-snapshots'
    and name = (public.jwt_dev() || '.jpg')
  );

drop policy if exists p_snap_driver_read on storage.objects;
create policy p_snap_driver_read on storage.objects
  for select to app_driver
  using (
    bucket_id = 'camera-snapshots'
    and name = (public.driver_active_camera() || '.jpg')
  );

commit;
