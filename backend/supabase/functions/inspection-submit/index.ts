// inspection-submit: records a pre-trip inspection and everything that follows from it.
// Auth: Authorization: Bearer <app_driver JWT> (self-verified, deploy --no-verify-jwt).
// Body: { trip_id, results: { "<label>": "Pass" | "Fail", ... }, notes? }
// Returns 200 { status, blocked, failed, critical } | 400 | 401.
//
// Criticality is read from checklist_items here, not sent by the phone: a modified build
// could otherwise report a failed brake as an ordinary defect. app_driver also holds no
// write on vehicles.out_of_service, the gate dispatch honours, so grounding belongs here.
//
// Faults join the vehicle's open order, or start one. A fault already listed re-opens
// rather than duplicating.

import { createClient } from "npm:@supabase/supabase-js@2";
import { CORS_HEADERS, json, verifyJwt } from "../_shared/auth.ts";
import { audit } from "../_shared/audit.ts";

const service = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

/** The column each configured section is stored in. */
const SECTION_COLUMNS = [
  "exterior_inspection",
  "engine_compartment",
  "interior_inspection",
  "brake_safety",
  "passenger_systems",
] as const;

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: CORS_HEADERS });
  if (req.method !== "POST") return json(405, { error: "POST only" });

  const secret = Deno.env.get("JWT_SECRET");
  if (!secret) return json(500, { error: "JWT_SECRET not configured" });

  const bearer = (req.headers.get("Authorization") ?? "").replace(/^Bearer\s+/i, "");
  const claims = bearer ? await verifyJwt(bearer, secret) : null;
  if (!claims || claims.role !== "app_driver" || typeof claims.user_id !== "number") {
    return json(401, { error: "Not signed in." });
  }
  const driverId = claims.user_id as number;

  let tripId: string, results: Record<string, string>, notes: string | null;
  try {
    const body = await req.json();
    tripId = String(body.trip_id ?? "").trim();
    results = (body.results ?? {}) as Record<string, string>;
    notes = body.notes ? String(body.notes).trim() : null;
  } catch {
    return json(400, { error: "Invalid JSON body" });
  }
  if (!tripId) return json(400, { error: "trip_id required" });

  // The trip names the bus. Taking the vehicle from the request would let a driver
  // inspect one bus and report it against another.
  const { data: tripRows } = await service
    .from("trips").select("trip_id, vehicle_id, driver_id").eq("trip_id", tripId).limit(1);
  const trip = tripRows?.[0];
  if (!trip) return json(400, { error: "Trip not found." });
  if (trip.driver_id !== driverId) return json(401, { error: "That trip belongs to someone else." });

  const vehicleId = trip.vehicle_id as string;

  const { data: configured } = await service
    .from("checklist_items")
    .select("item_id, label, is_critical, section_key")
    .eq("active", true);
  if (!configured || configured.length === 0) return json(400, { error: "No inspection is configured." });

  // Only configured items count. Anything else the caller sent is ignored rather than
  // trusted, and an item left unanswered counts as unchecked, not as passed.
  const failed = configured.filter((item) =>
    String(results[item.label] ?? "Fail").trim().toLowerCase() !== "pass"
  );
  const critical = failed.filter((item) => item.is_critical);
  const blocked = critical.length > 0;

  const status = blocked ? "Failed" : failed.length === 0 ? "Passed" : "Passed with Defects";

  // The inspection as submitted, one column per section, kept as the record of what was
  // checked at the time.
  const sections: Record<string, Record<string, string>> = {};
  for (const column of SECTION_COLUMNS) sections[column] = {};
  for (const item of configured) {
    const column = SECTION_COLUMNS.includes(item.section_key as never)
      ? item.section_key
      : "exterior_inspection";
    sections[column][item.label] =
      String(results[item.label] ?? "Fail").trim().toLowerCase() === "pass" ? "Pass" : "Fail";
  }

  const { data: insertedChecklist, error: clErr } = await service
    .from("bus_checklist")
    .insert({
      trip_id: tripId,
      vehicle_id: vehicleId,
      driver_id: driverId,
      submitted_at: new Date().toISOString(),
      ...sections,
      checklist_status: status,
      notes,
    })
    .select("checklist_id")
    .limit(1);
  if (clErr) return json(500, { error: "Could not record the inspection." });

  const checklistId = insertedChecklist?.[0]?.checklist_id ?? null;
  let orderId: number | null = null;

  if (failed.length > 0) {
    // One open order per bus. A bus already in the shop takes these faults onto the
    // order it has rather than starting another.
    const { data: openOrders } = await service
      .from("maintenance_logs")
      .select("log_id")
      .eq("vehicle_id", vehicleId)
      .is("resolved_at", null)
      .order("created_at", { ascending: true })
      .limit(1);

    orderId = openOrders?.[0]?.log_id ?? null;

    if (orderId === null) {
      const { data: created, error: logErr } = await service
        .from("maintenance_logs")
        .insert({
          checklist_id: checklistId,
          vehicle_id: vehicleId,
          trip_id: tripId,
          // Labels are what a person reads, and they are kept as they read on the day
          // this was raised, because an order describes faults somebody actually saw. The
          // ids are carried alongside so the same fault can still be recognised after its
          // label is reworded, which a label on its own cannot survive. Parallel arrays,
          // same order, rather than a different shape, so every existing reader of issues
          // and critical_issues keeps working untouched.
          issue_details: {
            issues: failed.map((f) => f.label),
            item_ids: failed.map((f) => f.item_id),
            severity: blocked ? "Critical" : "Minor",
            critical_issues: critical.map((f) => f.label),
            critical_item_ids: critical.map((f) => f.item_id),
          },
          maintenance_status: "Needs Attention",
          created_at: new Date().toISOString(),
        })
        .select("log_id")
        .limit(1);
      if (logErr) return json(500, { error: "Could not open a maintenance order." });
      orderId = created?.[0]?.log_id ?? null;
    }

    if (orderId !== null) {
      const { data: existing } = await service
        .from("maintenance_items")
        .select("item_id, label, state, checklist_item_id")
        .eq("log_id", orderId);

      // A fault coming back is recognised by the inspection item that raised it, which
      // survives the item being reworded. Lines raised before that was recorded, and
      // lines an admin typed by hand, carry no item and are still found by their label:
      // it is all they have ever had, and it holds as long as nobody edits the wording.
      //
      // Note the two meanings of item_id here. On a maintenance line it is the line's own
      // identifier; on a fault it is the checklist item's. They are never the same number.
      const byChecklistItem = new Map(
        (existing ?? [])
          .filter((i) => i.checklist_item_id !== null)
          .map((i) => [i.checklist_item_id, i]),
      );
      const byLabel = new Map(
        (existing ?? []).map((i) => [String(i.label).toLowerCase(), i]),
      );

      for (const fault of failed) {
        const already = byChecklistItem.get(fault.item_id)
          ?? byLabel.get(fault.label.toLowerCase());
        if (already) {
          // A fault reported again is open again, whatever it was closed as.
          if (already.state !== "open") {
            await service.from("maintenance_items")
              .update({ state: "open", closed_at: null, closed_by: null, note: null })
              .eq("item_id", already.item_id);
          }
        } else {
          await service.from("maintenance_items").insert({
            log_id: orderId,
            label: fault.label,
            checklist_item_id: fault.item_id,
            is_critical: fault.is_critical,
            source: "checklist",
            state: "open",
          });
        }
      }
    }
  }

  // Grounding is the only thing that stops the trip, and it belongs to the server for
  // the same reason criticality does.
  const vehiclePatch: Record<string, unknown> = { updated_at: new Date().toISOString() };
  if (blocked) vehiclePatch.out_of_service = true;
  vehiclePatch.vehicle_status = failed.length === 0 ? "Ready to Deploy" : "Flagged";
  await service.from("vehicles").update(vehiclePatch).eq("vehicle_id", vehicleId);

  await audit(req, {
    action: "inspection_submitted",
    actorType: "user",
    actorId: driverId,
    actorRole: "app_driver",
    targetTable: "vehicles",
    targetId: vehicleId,
    outcome: blocked ? "denied" : "ok",
    summary: blocked
      ? `Bus ${vehicleId} grounded by inspection: ${critical.map((c) => c.label).join(", ")}`
      : failed.length === 0
      ? `Bus ${vehicleId} passed inspection`
      : `Bus ${vehicleId} passed with defects: ${failed.map((f) => f.label).join(", ")}`,
  });

  return json(200, {
    status,
    blocked,
    failed: failed.map((f) => f.label),
    critical: critical.map((c) => c.label),
  });
});
