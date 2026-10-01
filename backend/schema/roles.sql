
SET default_transaction_read_only = off;

SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;

CREATE ROLE "app_camera";
ALTER ROLE "app_camera" WITH INHERIT NOCREATEROLE NOCREATEDB NOLOGIN NOBYPASSRLS;
CREATE ROLE "app_driver";
ALTER ROLE "app_driver" WITH INHERIT NOCREATEROLE NOCREATEDB NOLOGIN NOBYPASSRLS;

ALTER ROLE "anon" SET "statement_timeout" TO '3s';

ALTER ROLE "authenticated" SET "statement_timeout" TO '8s';

ALTER ROLE "authenticator" SET "statement_timeout" TO '8s';

ALTER ROLE "supabase_admin" SET "statement_timeout" TO '0';

RESET ALL;
