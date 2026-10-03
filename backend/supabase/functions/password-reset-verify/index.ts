// password-reset-verify: step two of recovering a forgotten password.
// Body: { email, otp } -> 200 { reset_token } | 400 | 401.
// Deploy with --no-verify-jwt (the caller is still locked out at this point).
//
// Trades a correct code for a ten-minute token that authorises exactly one
// password change. Splitting this from the change itself is a user-interface
// decision: the driver learns the code was wrong before typing a new password.
//
// Every rejection returns the same 401 text, so a wrong code, an expired code
// and an address with no account are indistinguishable.

import { createClient } from "npm:@supabase/supabase-js@2";
import {
  CORS_HEADERS,
  fixedTimeEquals,
  hmacHex,
  json,
  mintJwt,
  nowSec,
} from "../_shared/auth.ts";
import { audit } from "../_shared/audit.ts";
import { actorFor } from "../_shared/actor.ts";

const MAX_ATTEMPTS = 5;
const TOKEN_TTL_MIN = 10;
const REJECTED = "That code is invalid or has expired.";

const service = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: CORS_HEADERS });
  if (req.method !== "POST") return json(405, { error: "POST only" });

  const secret = Deno.env.get("JWT_SECRET");
  if (!secret) return json(500, { error: "JWT_SECRET not configured" });

  let email: string, otp: string;
  try {
    const body = await req.json();
    // Addresses are stored in lower case, so the one typed matches whatever its case.
    email = String(body.email ?? "").trim().toLowerCase();
    otp = String(body.otp ?? "").trim();
  } catch {
    return json(400, { error: "Invalid JSON body" });
  }
  if (!email || !otp) return json(400, { error: "email and otp required" });

  let actor: { actorType: string; actorRole: string | null } | null = null;

  const refuse = async (reason: string, userId?: number) => {
    await audit(req, {
      action: "password_reset_failed",
      actorType: actor?.actorType ?? (userId ? "user" : "anon"),
      actorRole: actor?.actorRole ?? null,
      actorId: userId ?? null,
      targetTable: "users",
      targetId: userId ?? null,
      outcome: "denied",
      summary: `Reset code refused for ${email} (${reason})`,
    });
    return json(401, { error: REJECTED });
  };

  const { data: userRows, error: userErr } = await service
    .from("users")
    .select("user_id, first_name, last_name, role_id, account_status")
    .eq("email_address", email)
    .limit(1);
  if (userErr) return json(500, { error: "Lookup failed" });

  const user = userRows?.[0];
  if (!user) return await refuse("no such account");
  actor = await actorFor(service, user.role_id);
  if (user.account_status !== "Activated") {
    return await refuse(`account ${user.account_status}`, user.user_id);
  }

  // The newest code that is still live: unspent, unexpired, and with guesses left.
  const { data: otpRows, error: otpErr } = await service
    .from("password_reset_otp")
    .select("id, otp_hash, attempts")
    .eq("user_id", user.user_id)
    .is("consumed_at", null)
    .gt("expires_at", new Date().toISOString())
    .lt("attempts", MAX_ATTEMPTS)
    .order("created_at", { ascending: false })
    .limit(1);
  if (otpErr) return json(500, { error: "Lookup failed" });

  const row = otpRows?.[0];
  if (!row) return await refuse("no live code", user.user_id);

  const enc = new TextEncoder();
  const expected = await hmacHex(secret, `otp:${user.user_id}:${otp}`);
  if (!fixedTimeEquals(enc.encode(expected), enc.encode(row.otp_hash))) {
    // Burning an attempt is what caps guessing at five tries per code. The row
    // is left in place so it keeps counting toward the request rate limits.
    await service
      .from("password_reset_otp")
      .update({ attempts: row.attempts + 1 })
      .eq("id", row.id);
    return await refuse(`wrong code, attempt ${row.attempts + 1} of ${MAX_ATTEMPTS}`, user.user_id);
  }

  // Marking the code spent here, not at the password change, means a code cannot
  // be replayed to mint a second token while the first is still valid.
  const { error: upErr } = await service
    .from("password_reset_otp")
    .update({ consumed_at: new Date().toISOString() })
    .eq("id", row.id)
    .is("consumed_at", null);
  if (upErr) return json(500, { error: "Update failed" });

  // The token names the row it came from, so the change step can check that this
  // particular reset has not already been spent.
  const resetToken = await mintJwt(
    {
      purpose: "pwd_reset",
      user_id: user.user_id,
      rid: row.id,
      iat: nowSec(),
      exp: nowSec() + TOKEN_TTL_MIN * 60,
    },
    secret,
  );

  await audit(req, {
    action: "password_reset_verified",
    actorType: actor.actorType,
    actorRole: actor.actorRole,
    actorId: user.user_id,
    targetTable: "users",
    targetId: user.user_id,
    summary: `Reset code accepted for ${email}`,
  });

  return json(200, { reset_token: resetToken });
});
