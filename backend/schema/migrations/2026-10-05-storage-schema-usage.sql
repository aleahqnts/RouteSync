-- Lets the app roles reach the storage schema.
--
-- The platform grants the storage schema to its own roles only. The policies in
-- storage.sql name app_driver and app_camera, but without usage on the schema those roles
-- are refused before any policy is consulted, so a driver's inspection photo and the
-- Sentinel's calibration snapshot were both turned away on the new project.

begin;

grant usage on schema storage to app_driver, app_camera;

commit;
