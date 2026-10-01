# Migrations since the last release

One script per database change made since the last release tag, named by date, with a
`-test.sql` and a `-rollback.sql` beside it. Each is applied by hand in the Supabase SQL
editor before the code that needs it is released.

Each release refreshes `../schema.sql` from the live database and deletes the scripts
here, so this folder is empty right after a release. A script from an earlier release is
still in the history at that release's tag.
