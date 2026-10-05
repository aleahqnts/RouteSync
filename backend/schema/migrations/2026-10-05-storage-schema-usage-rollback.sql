-- Backs out 2026-10-05-storage-schema-usage.sql. Inspection photos and calibration
-- snapshots stop uploading again once this runs.

begin;

revoke usage on schema storage from app_driver, app_camera;

commit;
