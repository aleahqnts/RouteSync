-- Checks for 2026-11-18-profile-photos.sql. Run after it; every row should say ok.
-- Nothing is left behind: the write checks run inside a transaction that is rolled back.

select case when exists (select 1 from information_schema.columns
  where table_schema = 'public' and table_name = 'users' and column_name = 'photo_key')
  then 'ok' else 'FAIL' end as users_photo_key_column;

select case when exists (select 1 from information_schema.columns
  where table_schema = 'public' and table_name = 'users_app' and column_name = 'photo_key')
  then 'ok' else 'FAIL' end as users_app_photo_key_column;

select case when has_column_privilege('app_driver', 'public.users_app', 'photo_key', 'UPDATE')
  then 'ok' else 'FAIL' end as app_driver_may_set_photo;

select case when exists (select 1 from storage.buckets
  where id = 'profile-photos' and not public and file_size_limit = 262144)
  then 'ok' else 'FAIL' end as private_bucket;

select case when count(*) = 3 then 'ok' else 'FAIL' end as driver_policies
from pg_policies where schemaname = 'storage' and policyname like 'p_profile_photo_driver_%';

-- The constraint takes a name in the owner's folder and refuses one in anyone else's.
begin;
do $$
declare uid int := (select min(user_id) from public.users);
begin
  update public.users set photo_key = uid || '/0123456789abcdef0123456789abcdef.jpg' where user_id = uid;
  begin
    update public.users set photo_key = (uid + 1) || '/0123456789abcdef0123456789abcdef.jpg' where user_id = uid;
    raise exception 'FAIL: a photo in another folder was accepted';
  exception when check_violation then null;
  end;
  raise notice 'ok: photo_key constraint';
end $$;
rollback;
