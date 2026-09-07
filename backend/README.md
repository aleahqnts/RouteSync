# Backend

The server side of RouteSync: edge functions running on Supabase, and the SQL that
shapes the database they talk to. All three clients (`RouteSyncWeb`, `RouteSyncMobile`,
`CameraCountMobile`) reach the fleet through these.

## Layout

| Path | Holds |
|------|-------|
| `supabase/functions/` | Deno edge functions, deployed to Supabase |
| `supabase/functions/_shared/` | JWT signing and verification, audit writer, mail, password rules |
| `schema/` | A dump of the live database: its tables, policies, grants, and roles |
| `schema/migrations/` | The change scripts applied by hand, so the dump can be explained |

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
the database rather than drive it. Refresh them after a schema change.

| File | Holds |
|------|-------|
| `schema.sql` | Tables, enums, functions, triggers, row-level security policies, grants |
| `roles.sql` | The `app_driver` and `app_camera` roles |
| `migrations/` | One script per change, with a test and a rollback beside it |

A dump says what the database looks like, not why. `migrations/` holds the script for
each change that was applied, dated, so a column with a rule behind it can be traced to
the reasoning that put it there. Apply the script, then refresh the dump.

Two dump files, because a schema dump does not include roles. Restoring `schema.sql` alone
would recreate policies that name roles nothing had created, so `roles.sql` runs first.

### Refreshing

The CLI runs `pg_dump` inside Docker. Without Docker, ask it to print the script it
would have run and run that instead, against a local `pg_dump` of version 17 or newer:

```
npx supabase@latest db dump --project-ref vrtluruqaxutecydbrsq --workdir backend --dry-run
```

Add `--role-only` for the second file. The printed script carries a freshly minted
database password, so keep it out of the repository. One correction is needed: it
abbreviates `--quote-all-identifiers`, which some builds reject.
