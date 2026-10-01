# Backend

The server side of RouteSync: edge functions running on Supabase, and the SQL that
shapes the database they talk to. All three clients (`RouteSyncWeb`, `RouteSyncMobile`,
`CameraCountMobile`) reach the fleet through these.

## Layout

| Path | Holds |
|------|-------|
| `supabase/functions/` | Deno edge functions, deployed to Supabase |
| `supabase/functions/_shared/` | JWT signing and verification, audit writer, mail, password rules |
| `schema/` | A dump of the live database: its tables, policies, grants, roles, and storage buckets |
| `schema/migrations/` | The change scripts applied by hand since the last release |

The `supabase/` directory keeps that exact name because the CLI looks for it by
convention. Everything else sits beside it.

## Deploying a function

Run from the repository root, naming this directory as the project:

```
npx supabase@latest functions deploy <name> --project-ref vrtluruqaxutecydbrsq --workdir backend --no-verify-jwt
```

`--no-verify-jwt` is right for every function here. Each one verifies its own bearer
token against `JWT_SECRET`, and the two that cannot require a token at all serve callers
who are locked out by definition: `auth-login` signs them in, and
`password-reset-request` mails a code to someone who has forgotten their password.

Secrets live in the Supabase project, not in this repository. A function reads them
through `Deno.env.get`.

## Schema

`schema/` is a snapshot of the live database, taken with `pg_dump`, not a migration
runner. Changes are made by hand through the Supabase SQL editor, so these files follow
the database rather than drive it. Refresh it after a schema change.

| File | Holds |
|------|-------|
| `schema.sql` | Tables, enums, functions, triggers, row-level security policies, grants |
| `migrations/` | One script per change since the last release, with a test and a rollback beside it |

A dump says what the database looks like, not why. `migrations/` holds the script for
each change made since the last release, dated, so a column with a rule behind it can be
traced to the reasoning that put it there. Apply the script, then refresh the dump.

Each release takes a fresh dump and clears `migrations/`, so the dump is the database as
of that release tag. A script from an earlier release is still in the history at that
release's tag.

The dump leaves out two things the live project has: the `app_driver` and `app_camera`
roles, and the storage buckets (`camera-snapshots`, `inspection-photos`) with their
policies. Restoring into a new project means creating those first, or the policies that
name the roles fail. Both are in the history at the v1.2.1 tag, as `roles.sql` and
`storage.sql`.

### Refreshing

The CLI runs `pg_dump` inside Docker. Without Docker, ask it to print the script it
would have run and run that instead, against a local `pg_dump` of version 17 or newer:

```
npx supabase@latest db dump --project-ref vrtluruqaxutecydbrsq --workdir backend --dry-run
```

The printed script carries a freshly minted database password, so keep it out of the
repository and out of anything shared. Each run mints a new one and retires the last, so
run a script straight after printing it. One correction is needed: it abbreviates
`--quote-all-identifiers`, which some builds reject.

