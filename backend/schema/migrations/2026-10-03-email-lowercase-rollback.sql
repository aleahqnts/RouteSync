-- Backs out 2026-10-03-email-lowercase.sql: addresses are no longer lowered on write.
-- The addresses already lowered stay as they are, since their old case was not kept and
-- lower case signs in either way once the dashboard and edge functions lower what is typed.

begin;

drop trigger if exists trg_users_email_lower on public.users;
drop function if exists public.users_email_lower();

commit;
