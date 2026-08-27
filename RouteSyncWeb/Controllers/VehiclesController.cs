using Microsoft.AspNetCore.Authorization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    [Authorize]
    [RequirePermission("vehicles")]
    public class VehiclesController : Controller
    {
        private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

        // Fixed filter vocabularies for the Status and Issues dropdowns.
        private static readonly string[] StatusFilterOptions =
            { "Ready to Deploy", "On Trip", "Pending", "Flagged", "Out of Service", "Retired" };

        private static readonly string[] ConditionFilterOptions =
            { "No Issues", "Needs Attention", "Under Repair" };

        private readonly Supabase.Client _supabase;
        private readonly AuditLog _audit;

        public VehiclesController(Supabase.Client supabase, AuditLog audit)
        {
            _supabase = supabase;
            _audit = audit;
        }

        public async Task<IActionResult> Index(string? route, string? status, string? condition, string? search)
        {
            var (vehicles, routes, maintenance) = await LoadVehicleDataAsync();

            var vm = new VehiclesIndexViewModel
            {
                // Rows load separately through VehicleRows, so the page appears immediately.
                Rows = new List<VehicleListItemViewModel>(),

                ActiveVehicles = vehicles.Count(v => v.RetiredAt == null),
                RetiredVehicles = vehicles.Count(v => v.RetiredAt != null),
                FlaggedVehicles = vehicles.Count(v => v.RetiredAt == null
                    && maintenance.GetValueOrDefault(v.VehicleId, "No Issues") != "No Issues"),
                ScheduledMaintenance = vehicles.Count(v => v.RetiredAt == null
                    && maintenance.TryGetValue(v.VehicleId, out var m) && m == "Under Repair"),

                RouteOptions = routes
                    .Select(r => new SelectListItem { Value = r.RouteId.ToString(), Text = r.RouteName })
                    .ToList(),
                StatusOptions = StatusFilterOptions.ToList(),
                ConditionOptions = ConditionFilterOptions.ToList(),
                NextVehicleId = NextVehicleId(vehicles),

                SelectedRoute = route,
                SelectedStatus = status,
                SelectedCondition = condition,
                SearchTerm = search,
            };

            SetModalViewData(vm, new AddVehicleViewModel(), openModal: null);
            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> VehicleRows(string? route, string? status, string? condition, string? search)
        {
            var items = await BuildRowsAsync(route, status, condition, search);
            return PartialView("_VehicleRows", items);
        }

        /// <summary>
        /// Sends a browser that arrived here by any means other than the form back to
        /// the registry.
        /// </summary>
        /// <remarks>
        /// A rejected submission renders the page at this address, so stepping back to
        /// it later is a plain GET. Without this the visitor meets an error page rather
        /// than the list they were looking at.
        /// </remarks>
        [HttpGet]
        public IActionResult Create() => RedirectToAction(nameof(Index));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(AddVehicleViewModel model)
        {
            if (!ModelState.IsValid)
                return await ReRenderIndexAsync(model);

            // Read at submit rather than when the form opened, so a bus added from
            // another seat in the meantime does not hand out the same number twice.
            var vehicleId = await NextVehicleIdAsync();

            var vehicle = new Vehicle
            {
                VehicleId = vehicleId,
                PlateNumber = model.PlateNumber.Trim(),
                RouteId = model.RouteId,
                Capacity = 50,                         // default; the form does not capture capacity
                VehicleStatus = "Ready to Deploy",     // new units start deployable (vehicle_status_enum label)
                CreatedAt = PhClock.Now,
            };

            await _supabase.From<Vehicle>().Insert(vehicle);

            await _audit.WriteAsync("vehicle_created",
                $"added bus {vehicle.VehicleId} (plate {vehicle.PlateNumber})",
                "vehicles", vehicle.VehicleId);

            TempData["Success"] = $"Vehicle \"{vehicleId}\" was added successfully.";
            return RedirectToAction(nameof(Index));
        }





        /// <summary>One item of a maintenance order, as posted by the update dialog.</summary>
        public sealed class MaintenanceItemUpdate
        {
            public long ItemId { get; set; }
            public string State { get; set; } = "open";
            public string? Note { get; set; }
        }

        /// <summary>
        /// Records progress on a maintenance order, and closes it when nothing is left.
        /// </summary>
        /// <remarks>
        /// Closing un-grounds the bus, which is why it happens on an administrator saving
        /// this dialog rather than on a checkbox: putting a bus back on the road is a
        /// decision someone makes, not a consequence of a tick count.
        ///
        /// A critical fault can only be closed by fixing it. Dismissing one would return a
        /// bus to service on an opinion that the fault was never real, so the database
        /// refuses it as well.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMaintenanceItems(int logId, string updates, string? note)
        {
            if (logId <= 0) return BadRequest("Invalid order.");

            List<MaintenanceItemUpdate>? posted;
            try
            {
                posted = System.Text.Json.JsonSerializer.Deserialize<List<MaintenanceItemUpdate>>(
                    updates ?? "[]",
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return BadRequest("Could not read the update.");
            }
            var remark = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if ((posted is null || posted.Count == 0) && remark is null)
                return BadRequest("Nothing to update.");
            posted ??= new List<MaintenanceItemUpdate>();

            var logResp = await _supabase.From<MaintenanceLog>()
                .Filter("log_id", Postgrest.Constants.Operator.Equals, logId.ToString())
                .Get();
            var order = logResp.Models.FirstOrDefault();
            if (order is null) return NotFound();
            if (order.ResolvedAt != null) return BadRequest("This order is already closed.");

            var itemResp = await _supabase.From<MaintenanceItem>()
                .Filter("log_id", Postgrest.Constants.Operator.Equals, logId.ToString())
                .Get();
            var items = itemResp.Models.ToDictionary(i => i.ItemId);

            var (uid, uname) = CurrentUser();
            var fixedLabels = new List<string>();
            var dismissedLabels = new List<string>();

            foreach (var change in posted)
            {
                if (!items.TryGetValue(change.ItemId, out var item)) continue;

                var state = (change.State ?? "open").Trim().ToLowerInvariant();
                if (state is not ("open" or "fixed" or "dismissed"))
                    return BadRequest($"\"{item.Label}\" was given an outcome that does not exist.");

                if (state == "dismissed")
                {
                    if (item.IsCritical)
                        return BadRequest($"\"{item.Label}\" grounds the bus, so it can only be closed by fixing it.");
                    if (string.IsNullOrWhiteSpace(change.Note))
                        return BadRequest($"Say why \"{item.Label}\" was not an issue.");
                }

                if (string.Equals(item.State, state, OIC)) continue;

                item.State = state;
                item.Note = state == "dismissed" ? change.Note!.Trim() : null;
                item.ClosedAt = state == "open" ? null : PhClock.Now;
                item.ClosedBy = state == "open" ? null : uname;
                await _supabase.From<MaintenanceItem>().Update(item);

                if (state == "fixed") fixedLabels.Add(item.Label);
                else if (state == "dismissed") dismissedLabels.Add(item.Label);
            }

            // Read back rather than trusting the loop, so a concurrent change cannot leave
            // an order closed with work still on it.
            var remaining = (await _supabase.From<MaintenanceItem>()
                    .Filter("log_id", Postgrest.Constants.Operator.Equals, logId.ToString())
                    .Get()).Models
                .Count(i => string.Equals(i.State, "open", OIC));

            var summary = new List<string>();
            if (fixedLabels.Count > 0) summary.Add($"Fixed: {string.Join(", ", fixedLabels)}");
            if (dismissedLabels.Count > 0) summary.Add($"Not an issue: {string.Join(", ", dismissedLabels)}");

            var changedNothing = summary.Count == 0;
            if (changedNothing && remark is null) return BadRequest("Nothing to update.");

            // An order raised before faults were tracked separately has nothing to tick,
            // so the remark is what ends it.
            var itemless = items.Count == 0;

            // A save that only carries a remark is progress on work still under way, and
            // reads as a comment. One that closes faults reports what it closed.
            await _supabase.From<MaintenanceNote>().Insert(new MaintenanceNote
            {
                LogId = logId,
                AuthorId = uid,
                AuthorName = uname,
                Action = changedNothing && !itemless ? "Comment"
                       : remaining == 0 ? "Maintenance Complete"
                       : "Maintenance Update",
                Note = string.Join(" ", new[] { string.Join(". ", summary), remark }
                    .Where(part => !string.IsNullOrWhiteSpace(part))),
                CreatedAt = PhClock.NowForDb,
            });

            // A remark on its own leaves the order exactly as it was, unless there was
            // never anything on it to close.
            if (remaining == 0 && (!changedNothing || itemless))
            {
                order.ResolvedAt = PhClock.Now;
                order.MaintenanceStatus = "No Issues";
                if (string.IsNullOrWhiteSpace(order.VerifiedBy)) order.VerifiedBy = uname;
                await _supabase.From<MaintenanceLog>().Update(order);

                var vResp = await _supabase.From<Vehicle>()
                    .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, order.VehicleId)
                    .Get();
                var vehicle = vResp.Models.FirstOrDefault();
                if (vehicle != null)
                {
                    // Closing this order does not speak for another. The bus returns only
                    // when nothing anywhere still grounds it.
                    if ((await OpenCriticalFaultsAsync(order.VehicleId)).Count == 0)
                    {
                        vehicle.OutOfService = false;
                        if (string.Equals(vehicle.VehicleStatus?.Trim(), "Flagged", OIC))
                            vehicle.VehicleStatus = "Ready to Deploy";
                    }
                    vehicle.LastMaintenanceDate = PhClock.Today;
                    vehicle.UpdatedAt = PhClock.Now;
                    await _supabase.From<Vehicle>().Update(vehicle);
                }
            }

            await _audit.WriteAsync(
                remaining == 0 && (!changedNothing || itemless) ? "maintenance_completed" : "maintenance_updated",
                remaining == 0 && (!changedNothing || itemless)
                    ? $"completed maintenance on bus {order.VehicleId}"
                        + (summary.Count > 0 ? $" ({string.Join("; ", summary)})" : "")
                    : $"updated maintenance on bus {order.VehicleId}"
                        + (summary.Count > 0 ? $" ({string.Join("; ", summary)})" : "")
                        + $", {remaining} still open",
                "vehicles", order.VehicleId);

            return Ok();
        }



        /// <summary>The bus's open faults that ground it, empty when nothing does.</summary>
        /// <remarks>
        /// Read across every open order rather than one, so no route back to service can
        /// miss a fault sitting on another.
        /// </remarks>
        private async Task<List<string>> OpenCriticalFaultsAsync(string vehicleId)
        {
            var openOrders = (await _supabase.From<MaintenanceLog>()
                    .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                    .Get()).Models
                .Where(l => l.ResolvedAt == null)
                .Select(l => l.LogId)
                .ToList();

            var faults = new List<string>();
            foreach (var logId in openOrders)
            {
                faults.AddRange((await _supabase.From<MaintenanceItem>()
                        .Filter("log_id", Postgrest.Constants.Operator.Equals, logId.ToString())
                        .Get()).Models
                    .Where(i => i.IsCritical && string.Equals(i.State, "open", OIC))
                    .Select(i => i.Label ?? ""));
            }
            return faults;
        }

        /// <summary>The bus's open order, or nothing when it has none.</summary>
        /// <remarks>
        /// Separate from the version that opens one, because a caller that may still turn
        /// the request away has to be able to look without writing.
        /// </remarks>
        private async Task<MaintenanceLog?> OpenOrderAsync(string vehicleId) =>
            (await _supabase.From<MaintenanceLog>()
                    .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                    .Get()).Models
                .Where(l => l.ResolvedAt == null)
                .OrderBy(l => l.CreatedAt)
                .FirstOrDefault();

        /// <summary>The bus's open order, opening one when it has none.</summary>
        /// <remarks>
        /// A bus has at most one open order. Work raised while it already has one joins
        /// that order rather than starting a second, which is what kept several open at
        /// once and hid all but the first.
        /// </remarks>
        private async Task<MaintenanceLog?> OpenOrderForAsync(string vehicleId, string workshopStatus)
        {
            var existing = await OpenOrderAsync(vehicleId);

            if (existing is not null)
            {
                // A bus already carrying faults and now booked into the shop is booked.
                if (workshopStatus == "Under Repair"
                    && !string.Equals(existing.MaintenanceStatus, workshopStatus, OIC))
                {
                    existing.MaintenanceStatus = workshopStatus;
                    await _supabase.From<MaintenanceLog>().Update(existing);
                }
                return existing;
            }

            var created = await _supabase.From<MaintenanceLog>().Insert(new MaintenanceLog
            {
                VehicleId = vehicleId,
                MaintenanceStatus = workshopStatus,
                IssueDetails = new MaintenanceIssueDetails(),
                CreatedAt = PhClock.NowForDb,
            });
            return created.Models.FirstOrDefault();
        }

        /// <summary>Puts faults on an order, one line each however often they are raised.</summary>
        /// <remarks>
        /// A label matching a configured inspection item carries that item's criticality.
        /// Anything typed by hand does not, so it never grounds a bus by itself.
        /// </remarks>
        private async Task AddOrderItemsAsync(int logId, IEnumerable<string> labels)
        {
            var wanted = labels
                .Select(l => (l ?? "").Trim())
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (wanted.Count == 0) return;

            var catalogue = (await _supabase.From<ChecklistItem>().Get()).Models
                .GroupBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var present = (await _supabase.From<MaintenanceItem>()
                    .Filter("log_id", Postgrest.Constants.Operator.Equals, logId.ToString())
                    .Get()).Models
                .ToDictionary(i => i.Label ?? "", i => i, StringComparer.OrdinalIgnoreCase);

            foreach (var label in wanted)
            {
                if (present.TryGetValue(label, out var already))
                {
                    // Raised again is open again, whatever it was closed as.
                    if (!string.Equals(already.State, "open", OIC))
                    {
                        already.State = "open";
                        already.ClosedAt = null;
                        already.ClosedBy = null;
                        already.Note = null;
                        await _supabase.From<MaintenanceItem>().Update(already);
                    }
                    continue;
                }

                catalogue.TryGetValue(label, out var configured);
                await _supabase.From<MaintenanceItem>().Insert(new MaintenanceItem
                {
                    LogId = logId,
                    Label = label,
                    IsCritical = configured?.IsCritical ?? false,
                    Source = configured is null ? "manual" : "checklist",
                    State = "open",
                    CreatedAt = PhClock.NowForDb,
                });
            }
        }

        /// <summary>The faults on one order, still open ones first.</summary>
        /// <remarks>
        /// Open items lead because they are the work left to do. Within each group the
        /// order is by criticality then by name, so what grounds the bus reads first.
        /// </remarks>
        private async Task<List<MaintenanceItemLineViewModel>> LoadOrderItemsAsync(int logId)
        {
            var byOrder = await LoadOrderItemsAsync(new[] { logId });
            return byOrder.TryGetValue(logId, out var items) ? items : new();
        }

        /// <summary>The faults on several orders, keyed by order.</summary>
        /// <remarks>
        /// Read in one request rather than one per order, so a bus with a long service
        /// record does not cost a round trip for each order it has been through.
        /// </remarks>
        private async Task<Dictionary<int, List<MaintenanceItemLineViewModel>>> LoadOrderItemsAsync(
            IEnumerable<int> logIds)
        {
            var ids = logIds.Distinct().ToList();
            if (ids.Count == 0) return new();

            var response = await _supabase.From<MaintenanceItem>()
                .Filter("log_id", Postgrest.Constants.Operator.In, ids.Cast<object>().ToList())
                .Get();

            return response.Models
                .GroupBy(i => i.LogId)
                .ToDictionary(g => g.Key, g => g
                    .Select(FormatOrderItem)
                    .OrderByDescending(i => i.IsOpen)
                    .ThenByDescending(i => i.IsCritical)
                    .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
                    .ToList());
        }

        /// <summary>One fault as it reads in the panel, with its outcome in words.</summary>
        private static MaintenanceItemLineViewModel FormatOrderItem(MaintenanceItem item)
        {
            var open = string.Equals(item.State, "open", OIC);
            return new MaintenanceItemLineViewModel(
                item.ItemId,
                item.Label ?? "",
                item.IsCritical,
                open,
                open ? "" : string.Equals(item.State, "fixed", OIC) ? "Fixed" : "Not an issue",
                item.ClosedBy ?? "",
                item.Note ?? "");
        }

        /// <summary>Everything recorded against one bus, newest first.</summary>
        /// <remarks>
        /// The audit trail already holds every action taken on a vehicle and who took it,
        /// including the ones that happen without an incident to hang a note on, so it is
        /// read directly rather than assembled from maintenance notes.
        ///
        /// Only the columns the panel shows are asked for. The row diff a trail entry can
        /// carry is the largest thing in it and is read on the audit page, not here.
        /// </remarks>
        private async Task<List<VehicleHistoryEntryViewModel>> BuildHistoryAsync(string vehicleId)
        {
            var (rows, _) = await _audit.QueryAsync(
                $"target_table=eq.vehicles&target_id=eq.{Uri.EscapeDataString(vehicleId)}"
                + "&select=occurred_at,actor_role,action,outcome,summary"
                + "&order=occurred_at.desc&limit=300");

            if (rows is null) return new();

            return rows
                .Select(r => new VehicleHistoryEntryViewModel(
                    r.OccurredAt.ToLocalTime().ToString("MMM d, yyyy h:mm tt"),
                    string.IsNullOrWhiteSpace(r.ActorRole) ? "Dashboard" : r.ActorRole,
                    r.Summary ?? r.Action,
                    !string.Equals(r.Outcome, "ok", OIC)))
                .ToList();
        }

        /// <summary>The inspection sections, in the order a bus is walked around.</summary>
        private static readonly string[] SectionKeys =
        {
            "exterior_inspection", "engine_compartment", "interior_inspection",
            "brake_safety", "passenger_systems",
        };

        private static readonly string[] SectionNames =
        {
            "Exterior Inspection", "Engine Compartment", "Interior Inspection",
            "Brake & Safety Systems", "Passenger & Fare Systems",
        };

        /// <summary>Where a section sits in that order, with anything unknown last.</summary>
        private static int SectionOrder(string? key)
        {
            var at = Array.IndexOf(SectionKeys, key ?? "");
            return at < 0 ? SectionKeys.Length : at;
        }

        private static string SectionName(string? key)
        {
            var at = Array.IndexOf(SectionKeys, key ?? "");
            return at < 0 ? "Other Checks" : SectionNames[at];
        }

        /// <summary>The next free bus number, as V001, V002 and so on.</summary>
        /// <remarks>
        /// Counts from the highest number already issued rather than from how many
        /// buses exist, so retiring one never hands its number to a new arrival and
        /// leaves two rows in the trail sharing an identifier.
        /// </remarks>
        private async Task<string> NextVehicleIdAsync()
        {
            var all = await _supabase.From<Vehicle>().Get();
            return NextVehicleId(all.Models);
        }

        /// <summary>The next free bus number for a known set of vehicles.</summary>
        private static string NextVehicleId(IEnumerable<Vehicle> vehicles)
        {
            var highest = vehicles
                .Select(v => v.VehicleId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => Regex.Match(id, @"^V(\d+)$", RegexOptions.IgnoreCase))
                .Where(m => m.Success)
                .Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();

            return $"V{highest + 1:D3}";
        }

        /// <summary>
        /// Renders the vehicle details modal: the profile, the most recent driver
        /// inspection, and the maintenance history.
        /// </summary>
        /// <remarks>Fetched per vehicle rather than sent with the registry page, which
        /// would carry this for every row.</remarks>
        [HttpGet]
        public async Task<IActionResult> Details(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return NotFound();

            var vehicleResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, id)
                .Get();
            var vehicle = vehicleResp.Models.FirstOrDefault();
            if (vehicle is null)
                return NotFound();

            // Route name, where the vehicle has a route assigned.
            var routeName = "—";
            if (vehicle.RouteId.HasValue)
            {
                var routeResp = await _supabase.From<BusRoute>()
                    .Filter("route_id", Postgrest.Constants.Operator.Equals, vehicle.RouteId.Value)
                    .Get();
                routeName = routeResp.Models.FirstOrDefault()?.RouteName ?? "—";
            }

            // The most recent inspection, with the driver who submitted it.
            var checklistResp = await _supabase.From<BusChecklist>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, id)
                .Order("submitted_at", Postgrest.Constants.Ordering.Descending)
                .Get();
            var checklist = checklistResp.Models.FirstOrDefault();

            UserModel driver = null;
            if (checklist != null)
            {
                var driverResp = await _supabase.From<UserModel>()
                    .Filter("user_id", Postgrest.Constants.Operator.Equals, checklist.DriverId)
                    .Get();
                driver = driverResp.Models.FirstOrDefault();
            }

            // Maintenance history, most recent first.
            var logsResp = await _supabase.From<MaintenanceLog>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, id)
                .Order("created_at", Postgrest.Constants.Ordering.Descending)
                .Get();
            var logs = logsResp.Models;

            var vm = new VehicleDetailsViewModel
            {
                VehicleId = vehicle.VehicleId,
                PlateNumber = vehicle.PlateNumber ?? "—",
                RouteName = routeName,
                CounterDeviceId = vehicle.CounterDeviceId,
            };

            if (checklist != null)
            {
                vm.HasInspection = true;
                vm.ReportedBy = DriverName(driver, checklist.DriverId);
                vm.TimeOfReport = checklist.SubmittedAt.ToString("MM/dd/yy hh:mm tt");
                vm.Issue = DeriveInspectionIssue(checklist);
                vm.InspectionSections = DeriveInspectionSections(checklist);
                var itemsResponse = await _supabase.From<ChecklistItem>().Get();
                vm.InspectionChecklist = DeriveInspectionChecklist(checklist, itemsResponse.Models);
                vm.InspectionBadge = DeriveInspectionBadge(checklist.ChecklistStatus);

                // The inspection flag comes from the driver's report, and resolving
                // maintenance never edits the checklist, so a repaired bus would otherwise
                // keep its flag and failed-item list indefinitely. Once every incident is
                // closed and the bus is back in service, the flag has been dealt with and
                // is cleared. The maintenance timeline still holds the history.
                //
                // A closed incident is required, meaning logs exist, none are open and the
                // bus is not grounded. A failed checklist with no maintenance log was never
                // acted on, so that stays flagged.
                bool hadIncident = logs.Count > 0;
                bool hasOpenIncident = logs.Any(l => l.ResolvedAt == null) || vehicle.OutOfService;
                if (hadIncident && !hasOpenIncident && vm.InspectionBadge == "Flagged")
                {
                    vm.InspectionBadge = "Resolved";
                    vm.Issue = "Resolved (issues addressed)";
                    vm.InspectionSections = new();
                }
            }

            // The maintenance panel reports what is wrong now. What went wrong before is
            // history, and is read from the audit trail below.
            vm.CurrentStatus = DeriveMaintenance(logs);
            var current = logs.FirstOrDefault(l => l.ResolvedAt == null);
            vm.OpenIncident = current is null ? null : FormatMaintenanceEntry(current);
            vm.HasMaintenance = current is not null;

            if (current is not null)
                vm.OpenOrderItems = await LoadOrderItemsAsync(current.LogId);

            // Offered when booking work, so a fault named here matches one a driver reports.
            vm.Catalogue = (await _supabase.From<ChecklistItem>().Get()).Models
                .Where(c => c.Active)
                .OrderBy(c => c.SortOrder)
                .GroupBy(c => c.SectionKey)
                .OrderBy(g => SectionOrder(g.Key))
                .Select(g => new InspectionResultSectionViewModel
                {
                    Section = SectionName(g.Key),
                    Items = g
                        .Select(c => new InspectionResultViewModel(c.Label, true, c.IsCritical))
                        .ToList(),
                })
                .ToList();

            vm.History = await BuildHistoryAsync(id);

            // Flag review: the out-of-service state, the incident to act on, and its
            // thread of comments and actions. The thread follows the open incident, or the
            // most recent one when nothing is open.
            vm.OutOfService = vehicle.OutOfService;
            vm.Retired = vehicle.RetiredAt != null;
            vm.OnTrip = await OnActiveTripAsync(vehicle.VehicleId);
            var openLog = logs.FirstOrDefault(l => l.ResolvedAt == null);
            vm.OpenLogId = openLog?.LogId;
            vm.OpenIncidentCritical = openLog?.IssueDetails?.IsCritical == true;
            vm.OpenIncidentSummary = openLog?.IssueDetails?.CriticalSummary ?? "";

            // History across every incident on this vehicle, not only the open one.
            // Limiting it to a single thread hides earlier notes as soon as a second
            // incident is raised.
            var logIds = logs.Select(l => l.LogId).ToHashSet();
            if (logIds.Count > 0)
            {
                var notesResp = await _supabase.From<MaintenanceNote>()
                    .Order("created_at", Postgrest.Constants.Ordering.Descending)
                    .Get();
                // Grouped by log so each incident's lifecycle, from flagged through to
                // resolved, forms one block. Newest incident first, and newest note first
                // within each.
                vm.IncidentThreads = notesResp.Models
                    .Where(n => logIds.Contains(n.LogId))
                    .GroupBy(n => n.LogId)
                    .OrderByDescending(g => g.Max(n => n.CreatedAt))
                    .Select(g => new VehicleIncidentThreadViewModel
                    {
                        LogId = g.Key,
                        Notes = g.OrderByDescending(n => n.CreatedAt)
                            .Select(n => new VehicleNoteViewModel
                            {
                                Action = string.IsNullOrWhiteSpace(n.Action) ? "Comment" : n.Action,
                                Note = n.Note ?? "",
                                AuthorName = string.IsNullOrWhiteSpace(n.AuthorName) ? "—" : n.AuthorName,
                                // The stored digits are Philippine wall-clock time; postgrest
                                // reads them eight hours ahead, so they are normalized back.
                                When = n.CreatedAt.ToUniversalTime().ToString("MM/dd/yy hh:mm tt"),
                            }).ToList()
                    }).ToList();
            }

            // Orders already closed. The panel above says what is wrong now; this says what
            // has been wrong before, and what was done about it.
            var closedOrders = logs
                .Where(l => l.ResolvedAt != null)
                .OrderByDescending(l => l.ResolvedAt)
                .ToList();

            if (closedOrders.Count > 0)
            {
                var itemsByOrder = await LoadOrderItemsAsync(closedOrders.Select(l => l.LogId));
                var notesByOrder = vm.IncidentThreads.ToDictionary(t => t.LogId, t => t.Notes);

                vm.PastOrders = closedOrders
                    .Select(l =>
                    {
                        var items = itemsByOrder.TryGetValue(l.LogId, out var found) ? found : new();

                        // An order raised before faults were tracked as rows has none, so the
                        // issue it recorded stands in for them.
                        var summary = items.Count > 0
                            ? string.Join(", ", items.Select(i => i.Label))
                            : FormatMaintenanceEntry(l).Summary;

                        return new PastOrderViewModel
                        {
                            LogId = l.LogId,
                            Opened = l.CreatedAt.ToString("MM/dd/yy"),
                            Closed = l.ResolvedAt!.Value.ToString("MM/dd/yy"),
                            Summary = summary,
                            Items = items,
                            Notes = notesByOrder.TryGetValue(l.LogId, out var notes) ? notes : new(),
                        };
                    })
                    .ToList();
            }

            return PartialView("_VehicleDetails", vm);
        }

        /// <summary>Renders the edit vehicle modal: the editable profile and the most
        /// recent maintenance log, fetched per vehicle.</summary>
        [HttpGet]
        public async Task<IActionResult> EditForm(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return NotFound();

            var vm = await BuildEditViewModelAsync(id, posted: null);
            if (vm is null)
                return NotFound();

            return PartialView("_EditVehicleForm", vm);
        }


        /// <summary>
        /// Takes a bus out of the fleet, or brings it back.
        /// </summary>
        /// <remarks>
        /// The row survives, because trips, inspections, maintenance logs and the audit
        /// trail all key on the identifier and would otherwise point at nothing. A
        /// retired bus is absent from the registry, the counts and every assignment
        /// list, so it cannot be scheduled by accident.
        ///
        /// Retiring also grounds the bus, since the two answers to "can this run today"
        /// must not disagree.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRetired(string vehicleId, bool retired, string? reason)
        {
            if (string.IsNullOrWhiteSpace(vehicleId)) return BadRequest("Vehicle required.");

            var response = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Get();

            var vehicle = response.Models.FirstOrDefault();
            if (vehicle is null) return BadRequest("Vehicle not found.");

            if (retired)
            {
                // A bus on a trip right now cannot be retired from under its driver.
                if (await OnActiveTripAsync(vehicleId))
                    return BadRequest($"{vehicleId} is on a trip. End the trip before retiring it.");

                // A counter left attached would keep reporting against a bus that is no
                // longer in the fleet, and the phone could not be given to another one.
                if (!string.IsNullOrWhiteSpace(vehicle.CounterDeviceId))
                    return BadRequest($"{vehicleId} still has counter {vehicle.CounterDeviceId} attached. "
                                      + "Remove the counter before retiring it.");

                vehicle.RetiredAt = PhClock.Now;
                vehicle.RetiredReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
                // Grounding is the out_of_service flag alone. vehicle_status carries the
                // shift's own labels and has none for this.
                vehicle.OutOfService = true;
            }
            else
            {
                vehicle.RetiredAt = null;
                vehicle.RetiredReason = null;
                // The bus stays grounded on purpose: whether it is roadworthy is a
                // separate decision, made in the review panel.
            }

            vehicle.UpdatedAt = PhClock.Now;
            await _supabase.From<Vehicle>().Update(vehicle);

            await _audit.WriteAsync(
                retired ? "vehicle_retired" : "vehicle_restored",
                retired
                    ? $"retired bus {vehicleId} from the fleet"
                        + (string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason.Trim()})")
                    : $"restored bus {vehicleId} to the fleet",
                "vehicles", vehicleId);

            return Ok();
        }

        /// <inheritdoc cref="Create()"/>
        [HttpGet]
        public IActionResult Edit() => RedirectToAction(nameof(Index));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(EditVehicleViewModel model)
        {
            if (!ModelState.IsValid)
                return await ReRenderIndexForEditAsync(model);

            var vehicleResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, model.VehicleId)
                .Get();
            var vehicle = vehicleResp.Models.FirstOrDefault();
            if (vehicle is null)
            {
                TempData["Error"] = "Vehicle not found.";
                return RedirectToAction(nameof(Index));
            }

            // Profile fields only. Vehicle type is left alone because every unit is a bus.
            // The incident lifecycle belongs entirely to the actions on the details modal,
            // so editing never changes maintenance state.
            vehicle.PlateNumber = model.PlateNumber.Trim();
            vehicle.RouteId = model.RouteId;
            vehicle.UpdatedAt = PhClock.Now;

            await _supabase.From<Vehicle>().Update(vehicle);

            await _audit.WriteAsync("vehicle_updated",
                $"edited bus {model.VehicleId} (plate {vehicle.PlateNumber})",
                "vehicles", model.VehicleId);

            TempData["Success"] = $"Vehicle \"{model.VehicleId}\" was updated successfully.";
            return RedirectToAction(nameof(Index));
        }

        // Flag review actions (from the View Vehicle modal).



        /// <summary>Grounds a bus so dispatch cannot assign it, or returns it to service.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        /// <summary>Whether the bus is out on a trip at this moment.</summary>
        /// <remarks>
        /// Asked before retiring a bus and before grounding one. Both take it off the
        /// road, and neither may do so while it is carrying passengers.
        /// </remarks>
        private async Task<bool> OnActiveTripAsync(string vehicleId) =>
            (await _supabase.From<Trip>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Filter("trip_status", Postgrest.Constants.Operator.Equals, "Active")
                .Get()).Models.Count > 0;

        public async Task<IActionResult> SetServiceState(string vehicleId, bool outOfService, int? logId, string? note)
        {
            if (string.IsNullOrWhiteSpace(vehicleId)) return BadRequest("Vehicle required.");

            var vResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Get();
            var vehicle = vResp.Models.FirstOrDefault();
            if (vehicle is null) return NotFound();

            var (uid, uname) = CurrentUser();
            int? effectiveLog = logId;

            if (!outOfService)
            {
                // A retired bus is out of the fleet, and being roadworthy does not put it
                // back. Returning one to service would leave it off the fleet yet free to
                // be dispatched, so restoring it comes first.
                if (vehicle.RetiredAt != null)
                    return BadRequest(
                        $"{vehicleId} is retired. Restore it to the fleet under Edit before "
                        + "returning it to service.");

                // A fault that grounds the bus is what keeps it grounded. Returning it to
                // service while one is open would put an unroadworthy bus back on the road
                // by a route that never asked whether the fault was dealt with. Fixing it
                // under Update Maintenance is what returns the bus.
                var criticalOpen = await OpenCriticalFaultsAsync(vehicleId);

                if (criticalOpen.Count > 0)
                    return BadRequest(
                        $"{vehicleId} still has faults that ground it: {string.Join(", ", criticalOpen)}. "
                        + "Fix them under Update Maintenance to return it to service.");
            }

            if (outOfService)
            {
                // Grounding a bus mid-route would strand its driver with passengers
                // aboard, and leave dispatch showing a bus that cannot be assigned to
                // the trip it is already running.
                if (await OnActiveTripAsync(vehicleId))
                    return BadRequest(
                        $"{vehicleId} is on a trip. End the trip before taking it off the road.");

                // What the bus already carries, looked at rather than opened. An order
                // raised for a request that is then turned away stays behind with nothing
                // on it: the bus reads as needing attention, and Update Maintenance has no
                // item to close, so nothing clears it.
                var existing = await OpenOrderAsync(vehicleId);
                var hasItems = existing is not null
                    && (await _supabase.From<MaintenanceItem>()
                            .Filter("log_id", Postgrest.Constants.Operator.Equals, existing.LogId.ToString())
                            .Get()).Models.Count > 0;

                // A bus grounded with nothing on its list has no record of why, and
                // nothing to close when it comes back. The reason becomes its first item,
                // which is why it is asked for rather than optional.
                if (!hasItems && string.IsNullOrWhiteSpace(note))
                    return BadRequest("Say why this bus is being taken out of service.");

                // One order carries the grounding, whether the bus already had faults
                // or not. Booking it into the workshop is Schedule Maintenance, which is
                // what promotes the order to under repair.
                var order = existing ?? await OpenOrderForAsync(vehicleId, "Needs Attention");
                if (order is null) return BadRequest("Could not open a maintenance order.");

                if (!hasItems) await AddOrderItemsAsync(order.LogId, new[] { note!.Trim() });

                effectiveLog = order.LogId;
            }

            vehicle.OutOfService = outOfService;
            vehicle.UpdatedAt = PhClock.Now;
            await _supabase.From<Vehicle>().Update(vehicle);

            if (effectiveLog is int lg)
            {
                await _supabase.From<MaintenanceNote>().Insert(new MaintenanceNote
                {
                    LogId = lg,
                    AuthorId = uid,
                    AuthorName = uname,
                    Action = outOfService ? "Out of Service" : "Returned to Service",
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    CreatedAt = PhClock.NowForDb,
                });
            }

            var reason = string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}";
            await _audit.WriteAsync(
                outOfService ? "vehicle_grounded" : "vehicle_returned",
                outOfService
                    ? $"took bus {vehicleId} out of service{reason}"
                    : $"returned bus {vehicleId} to service{reason}",
                "vehicles", vehicleId);

            return Ok();
        }

        /// <summary>
        /// Puts a bus into scheduled maintenance: opens an under-repair incident and
        /// grounds the bus, since a bus in the workshop is off the road.
        /// </summary>
        /// <remarks>Feeds the scheduled maintenance figure, appears in the vehicle's
        /// history, and keeps the bus out of dispatch and the schedule planner.</remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ScheduleMaintenance(string vehicleId, string? note, string? items)
        {
            if (string.IsNullOrWhiteSpace(vehicleId)) return BadRequest("Vehicle required.");

            List<string>? labels;
            try
            {
                labels = System.Text.Json.JsonSerializer.Deserialize<List<string>>(items ?? "[]");
            }
            catch
            {
                return BadRequest("Could not read the work list.");
            }

            labels ??= new List<string>();
            if (labels.Count == 0)
                return BadRequest("Say what the bus is going in for.");

            var (uid, uname) = CurrentUser();

            var order = await OpenOrderForAsync(vehicleId, "Under Repair");
            if (order is null) return BadRequest("Could not open a maintenance order.");

            await AddOrderItemsAsync(order.LogId, labels);

            await _supabase.From<MaintenanceNote>().Insert(new MaintenanceNote
            {
                LogId = order.LogId,
                AuthorId = uid,
                AuthorName = uname,
                Action = "Scheduled Maintenance",
                Note = string.IsNullOrWhiteSpace(note)
                    ? string.Join(", ", labels)
                    : note.Trim(),
                CreatedAt = PhClock.NowForDb,
            });

            // A bus in the workshop is off the road.
            var vResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Get();
            var vehicle = vResp.Models.FirstOrDefault();
            if (vehicle != null)
            {
                vehicle.OutOfService = true;
                vehicle.UpdatedAt = PhClock.Now;
                await _supabase.From<Vehicle>().Update(vehicle);
            }

            await _audit.WriteAsync("maintenance_scheduled",
                $"sent bus {vehicleId} to maintenance for {string.Join(", ", labels)}"
                    + (string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}"),
                "vehicles", vehicleId);

            return Ok();
        }

        /// <summary>The signed-in operator, recorded against thread entries.</summary>
        private (int? Id, string Name) CurrentUser()
        {
            var idStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int? id = int.TryParse(idStr, out var i) ? i : null;
            var name = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                       ?? User.Identity?.Name ?? "Admin";
            return (id, name);
        }

        // Data loading & projection.

        private async Task<(List<Vehicle> Vehicles, List<BusRoute> Routes, Dictionary<string, string> Maintenance)> LoadVehicleDataAsync()
        {
            var vehiclesResponse = await _supabase.From<Vehicle>().Get();
            var routesResponse = await _supabase
                .From<BusRoute>()
                .Order("route_name", Postgrest.Constants.Ordering.Ascending)
                .Get();
            var logsResponse = await _supabase.From<MaintenanceLog>().Get();

            var logsByVehicle = logsResponse.Models
                .Where(l => l.VehicleId != null)
                .GroupBy(l => l.VehicleId)
                .ToDictionary(g => g.Key, g => g.AsEnumerable());

            // The maintenance badge shows the most recent unresolved log per vehicle, or
            // no issues when nothing is open.
            var maintenance = vehiclesResponse.Models.ToDictionary(
                v => v.VehicleId,
                v => DeriveMaintenance(logsByVehicle.TryGetValue(v.VehicleId, out var logs)
                    ? logs
                    : Enumerable.Empty<MaintenanceLog>()));

            return (vehiclesResponse.Models, routesResponse.Models, maintenance);
        }

        private async Task<List<VehicleListItemViewModel>> BuildRowsAsync(string? route, string? status, string? condition, string? search)
        {
            var (vehicles, routes, maintenance) = await LoadVehicleDataAsync();
            var routeNames = routes.ToDictionary(r => r.RouteId, r => r.RouteName);

            // A bus counts as on trip only while it has an active trip. The stored
            // vehicle_status column is set when a trip starts and cleared only by the
            // driver app ending it, so a trip that is removed, rolled over, or finished
            // outside the app leaves the column stuck. Deriving the state from live trips,
            // as the dispatch board and fleet map do, corrects itself instead.
            var activeVehicleIds = (await _supabase.From<Trip>()
                    .Filter("trip_status", Postgrest.Constants.Operator.Equals, "Active")
                    .Get()).Models
                .Where(t => t.VehicleId != null)
                .Select(t => t.VehicleId)
                .ToHashSet();

            // Roadworthiness takes precedence in the registry's status column. An open
            // incident shows as flagged, otherwise the bus shows as on trip when one is
            // running, or its operational status. A stored flag or trip state with no
            // incident or trip behind it reads as ready.
            string RoadStatus(Vehicle v) =>
                v.RetiredAt != null ? "Retired"
                : v.OutOfService ? "Out of Service"
                : maintenance.GetValueOrDefault(v.VehicleId, "No Issues") != "No Issues" ? "Flagged"
                : activeVehicleIds.Contains(v.VehicleId) ? "On Trip"
                : NonTripStatus(v.VehicleStatus);

            IEnumerable<Vehicle> filtered = vehicles;

            if (!string.IsNullOrWhiteSpace(route) && int.TryParse(route, out var routeId))
                filtered = filtered.Where(v => v.RouteId == routeId);

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (string.Equals(status, "Flagged", OIC))
                    // An out-of-service bus is a flagged one that was grounded, so it stays
                    // in the flagged filter even though its badge reads out of service.
                    filtered = filtered.Where(v => RoadStatus(v) is "Flagged" or "Out of Service");
                else
                    filtered = filtered.Where(v => string.Equals(RoadStatus(v), status, OIC));
            }

            if (!string.IsNullOrWhiteSpace(condition))
                filtered = filtered.Where(v =>
                    string.Equals(maintenance.GetValueOrDefault(v.VehicleId, "No Issues"), condition, OIC));

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                filtered = filtered.Where(v =>
                    (v.VehicleId?.Contains(term, OIC) ?? false) ||
                    (v.PlateNumber?.Contains(term, OIC) ?? false));
            }

            return filtered
                .OrderBy(v => v.VehicleId, StringComparer.OrdinalIgnoreCase)
                .Select(v => new VehicleListItemViewModel
                {
                    VehicleId = v.VehicleId,
                    PlateNumber = v.PlateNumber ?? "",
                    RouteName = v.RouteId.HasValue && routeNames.TryGetValue(v.RouteId.Value, out var rn) ? rn : "—",
                    Status = RoadStatus(v),
                    Maintenance = maintenance.GetValueOrDefault(v.VehicleId, "No Issues"),
                })
                .ToList();
        }

        /// <summary>
        /// Re-renders the registry with the add vehicle modal open and its validation
        /// errors shown.
        /// </summary>
        /// <remarks>A redirect cannot carry model state, so a failed post returns the view
        /// directly rather than redirecting.</remarks>
        private async Task<IActionResult> ReRenderIndexAsync(AddVehicleViewModel addModel)
        {
            var (vehicles, routes, maintenance) = await LoadVehicleDataAsync();

            var vm = new VehiclesIndexViewModel
            {
                Rows = new List<VehicleListItemViewModel>(),
                ActiveVehicles = vehicles.Count(v => v.RetiredAt == null),
                RetiredVehicles = vehicles.Count(v => v.RetiredAt != null),
                FlaggedVehicles = vehicles.Count(v => v.RetiredAt == null
                    && maintenance.GetValueOrDefault(v.VehicleId, "No Issues") != "No Issues"),
                ScheduledMaintenance = vehicles.Count(v => v.RetiredAt == null
                    && maintenance.TryGetValue(v.VehicleId, out var um) && um == "Under Repair"),
                RouteOptions = routes
                    .Select(r => new SelectListItem { Value = r.RouteId.ToString(), Text = r.RouteName })
                    .ToList(),
                StatusOptions = StatusFilterOptions.ToList(),
                ConditionOptions = ConditionFilterOptions.ToList(),
                NextVehicleId = NextVehicleId(vehicles),
            };

            SetModalViewData(vm, addModel, openModal: "AddVehicle");
            return View("Index", vm);
        }

        /// <summary>Builds the edit vehicle modal's model: the editable profile and the
        /// route list.</summary>
        /// <param name="posted">Values from a failed submission, preserved so the operator
        /// does not lose what they typed.</param>
        private async Task<EditVehicleViewModel?> BuildEditViewModelAsync(string id, EditVehicleViewModel? posted)
        {
            var vehicleResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, id)
                .Get();
            var vehicle = vehicleResp.Models.FirstOrDefault();
            if (vehicle is null)
                return null;

            var routes = (await _supabase.From<BusRoute>()
                .Order("route_name", Postgrest.Constants.Ordering.Ascending)
                .Get()).Models;

            return new EditVehicleViewModel
            {
                VehicleId = vehicle.VehicleId,
                PlateNumber = posted?.PlateNumber ?? vehicle.PlateNumber ?? "",
                RouteId = posted?.RouteId ?? vehicle.RouteId ?? 0,
                RouteOptions = BuildRouteOptions(routes),
                Retired = vehicle.RetiredAt != null,
                RetiredReason = vehicle.RetiredReason,
            };
        }

        /// <summary>Re-renders the registry with the edit modal open and its validation
        /// errors shown, in the same way as the add path.</summary>
        private async Task<IActionResult> ReRenderIndexForEditAsync(EditVehicleViewModel editModel)
        {
            var (vehicles, routes, maintenance) = await LoadVehicleDataAsync();

            var vm = new VehiclesIndexViewModel
            {
                Rows = new List<VehicleListItemViewModel>(),
                ActiveVehicles = vehicles.Count(v => v.RetiredAt == null),
                RetiredVehicles = vehicles.Count(v => v.RetiredAt != null),
                FlaggedVehicles = vehicles.Count(v => v.RetiredAt == null
                    && maintenance.GetValueOrDefault(v.VehicleId, "No Issues") != "No Issues"),
                ScheduledMaintenance = vehicles.Count(v => v.RetiredAt == null
                    && maintenance.TryGetValue(v.VehicleId, out var m) && m == "Under Repair"),
                RouteOptions = BuildRouteOptions(routes),
                StatusOptions = StatusFilterOptions.ToList(),
                ConditionOptions = ConditionFilterOptions.ToList(),
            };

            SetModalViewData(vm, new AddVehicleViewModel(), openModal: "EditVehicle");
            ViewBag.EditVehicleModel = await BuildEditViewModelAsync(editModel.VehicleId, editModel);
            return View("Index", vm);
        }

        private static List<SelectListItem> BuildRouteOptions(IEnumerable<BusRoute> routes) =>
            routes
                .Select(r => new SelectListItem { Value = r.RouteId.ToString(), Text = r.RouteName })
                .ToList();

        /// <summary>Supplies the add vehicle modal with its model, dropdown data and
        /// reopen flag.</summary>
        private void SetModalViewData(VehiclesIndexViewModel vm, AddVehicleViewModel addModel, string? openModal)
        {
            ViewBag.AddVehicleModel = addModel;
            ViewBag.RouteOptions = vm.RouteOptions;
            ViewBag.OpenModal = openModal;
            // A preview only. The number is claimed at submit, not here.
            ViewBag.NextVehicleId = vm.NextVehicleId;
        }

        private static string DeriveMaintenance(IEnumerable<MaintenanceLog> logs)
        {
            var open = logs
                .Where(l => l.ResolvedAt == null)
                .OrderByDescending(l => l.CreatedAt)
                .FirstOrDefault();

            return open is null ? "No Issues" : NormalizeMaintenance(open.MaintenanceStatus);
        }

        /// <summary>
        /// Maps a stored maintenance status onto the two badges used for open incidents.
        /// </summary>
        /// <remarks>An unresolved log always means there is something to act on, so an
        /// unrecognized or empty status becomes "Needs Attention".</remarks>
        private static string NormalizeMaintenance(string? maintenanceStatus)
        {
            var s = (maintenanceStatus ?? "").Trim();
            if (s.Contains("Repair", OIC)) return "Under Repair";
            if (s.Contains("No Issue", OIC) || s.Contains("Resolved", OIC)) return "No Issues";
            return "Needs Attention";
        }

        /// <summary>
        /// The checklist sections containing at least one failed item, or "None" when
        /// everything passed.
        /// </summary>
        /// <remarks>Deliberately section-level. The maintenance issue summary lists the
        /// individual failed items, so the two complement each other rather than showing
        /// the same list twice.</remarks>
        private static string DeriveInspectionIssue(BusChecklist c)
        {
            var sections = new (string Name, Dictionary<string, string> Map)[]
            {
                ("Exterior Inspection", c.ExteriorInspection),
                ("Engine Compartment", c.EngineCompartment),
                ("Interior Inspection", c.InteriorInspection),
                ("Brake & Safety Systems", c.BrakeSafety),
                ("Passenger & Fare Systems", c.PassengerSystems),
            };

            var failed = sections
                .Where(s => s.Map != null && s.Map.Any(kv => !string.Equals(kv.Value?.Trim(), "Pass", OIC)))
                .Select(s => s.Name)
                .ToList();

            return failed.Count > 0 ? string.Join(", ", failed) : "None";
        }

        /// <summary>
        /// The badge for a checklist status. The stored enumeration has no flagged value,
        /// so a failure is shown as flagged and every other status is shown as stored.
        /// </summary>
        private static string DeriveInspectionBadge(string checklistStatus)
        {
            var s = (checklistStatus ?? "").Trim();
            // A critical failure grounds the bus; defects leave it deployable but worth
            // a look. Both read as flagged here, since both open an incident.
            if (s.Equals("Failed", OIC)) return "Flagged";
            if (s.Equals("Passed with Defects", OIC)) return "Defects";
            return string.IsNullOrEmpty(s) ? "Pending" : s;
        }

        /// <summary>
        /// Rewrites a failed checklist item into the problem it describes.
        /// </summary>
        /// <remarks>
        /// Some items were phrased negatively, where passing meant the absence of a fault.
        /// Listing those unchanged under failures inverts their meaning.
        ///
        /// The catalogue no longer holds any of them, but inspections recorded while it did
        /// are kept as they were submitted, so the wording they used is still read back.
        /// </remarks>
        private static readonly Dictionary<string, string> IssuePhrase = new(StringComparer.OrdinalIgnoreCase)
        {
            ["No Visible Body Damage"] = "Visible body damage",
            ["No fluid leaks under bus"] = "Fluid leak under bus",
            ["No unusual smoke or overheating"] = "Unusual smoke / overheating",
            ["No visible damage or leaks"] = "Visible damage or leaks",
        };

        private static string RephraseIssue(string issue) =>
            IssuePhrase.TryGetValue(issue?.Trim() ?? "", out var p) ? p : issue;

        /// <summary>
        /// Failed checklist items, rewritten and grouped by section, for the detail shown
        /// beneath the inspection's issue areas. Sections without a failure are omitted.
        /// </summary>
        private static List<InspectionSectionViewModel> DeriveInspectionSections(BusChecklist c)
        {
            var sections = new (string Name, Dictionary<string, string> Map)[]
            {
                ("Exterior Inspection", c.ExteriorInspection),
                ("Engine Compartment", c.EngineCompartment),
                ("Interior Inspection", c.InteriorInspection),
                ("Brake & Safety Systems", c.BrakeSafety),
                ("Passenger & Fare Systems", c.PassengerSystems),
            };

            var result = new List<InspectionSectionViewModel>();
            foreach (var s in sections)
            {
                if (s.Map is null) continue;
                var failed = s.Map
                    .Where(kv => !string.Equals(kv.Value?.Trim(), "Pass", OIC))
                    .Select(kv => RephraseIssue(kv.Key))
                    .ToList();
                if (failed.Count > 0)
                    result.Add(new InspectionSectionViewModel { Section = s.Name, Items = failed });
            }
            return result;
        }

        /// <summary>
        /// Every inspected item with how the driver marked it, grouped by section.
        /// </summary>
        /// <remarks>
        /// The flagged list answers what went wrong. This answers what was checked,
        /// which is the question asked of an inspection that passed.
        /// </remarks>
        private static List<InspectionResultSectionViewModel> DeriveInspectionChecklist(
            BusChecklist c, IReadOnlyCollection<ChecklistItem> configured)
        {
            var sections = new (string Key, string Name, Dictionary<string, string> Map)[]
            {
                (SectionKeys[0], SectionNames[0], c.ExteriorInspection),
                (SectionKeys[1], SectionNames[1], c.EngineCompartment),
                (SectionKeys[2], SectionNames[2], c.InteriorInspection),
                (SectionKeys[3], SectionNames[3], c.BrakeSafety),
                (SectionKeys[4], SectionNames[4], c.PassengerSystems),
            };

            // The order and the weight of each line come from the configured items, since
            // the stored inspection carries neither.
            var order = configured
                .GroupBy(i => i.Label)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var result = new List<InspectionResultSectionViewModel>();
            foreach (var s in sections)
            {
                if (s.Map is null || s.Map.Count == 0) continue;

                var items = s.Map
                    .Select(kv =>
                    {
                        order.TryGetValue(kv.Key, out var config);
                        return new
                        {
                            // An item retired since the inspection has no configured
                            // position, so it sorts after the ones that do.
                            Sort = config?.SortOrder ?? int.MaxValue,
                            Result = new InspectionResultViewModel(
                                kv.Key,
                                string.Equals(kv.Value?.Trim(), "Pass", OIC),
                                config?.IsCritical ?? false),
                        };
                    })
                    .OrderBy(x => x.Sort)
                    .ThenBy(x => x.Result.Item, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Result)
                    .ToList();

                result.Add(new InspectionResultSectionViewModel { Section = s.Name, Items = items });
            }
            return result;
        }

        /// <summary>
        /// One timeline entry per log: when it happened, what the issue was in plain words,
        /// and how it ended. The internal log reference is omitted, since it means nothing
        /// to an operator.
        /// </summary>
        private static MaintenanceEntryViewModel FormatMaintenanceEntry(MaintenanceLog log)
        {
            var summary = log.IssueDetails?.Issues is { Count: > 0 } issues
                ? string.Join(", ", issues.Select(RephraseIssue))
                : (string.IsNullOrWhiteSpace(log.Remarks) ? "Maintenance" : log.Remarks.Trim());

            return new MaintenanceEntryViewModel
            {
                Date = (log.ResolvedAt ?? log.CreatedAt).ToString("MM/dd/yy"),
                Summary = summary,
                Status = log.ResolvedAt != null
                    ? "Resolved"
                    : (string.IsNullOrWhiteSpace(log.MaintenanceStatus) ? "Open" : log.MaintenanceStatus.Trim()),
                IsResolved = log.ResolvedAt != null,
            };
        }

        private static string DriverName(UserModel driver, int driverId)
        {
            if (driver is null) return $"Driver {driverId}";
            var name = string.Join(" ",
                new[] { driver.FirstName, driver.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(name) ? $"Driver {driverId}" : name;
        }

        /// <summary>
        /// The status of a bus with no active trip. A stored on-trip or flagged value is
        /// stale in that case, from a trip that ended or a flag since resolved, and
        /// collapses to ready. Everything else maps through <see cref="DisplayStatus"/>.
        /// </summary>
        private static string NonTripStatus(string? vehicleStatus)
        {
            var s = (vehicleStatus ?? "").Trim();
            if (s.Equals("Flagged", OIC) || s.Equals("OnTrip", OIC) || s.Equals("On Trip", OIC) || s.Equals("Active", OIC))
                return "Ready to Deploy";
            return DisplayStatus(s);
        }

        /// <summary>
        /// Normalizes a stored vehicle status to the registry's labels, using the same
        /// vocabulary as the fleet map, where several stored spellings all mean the bus is
        /// on a live trip.
        /// </summary>
        private static string DisplayStatus(string? vehicleStatus)
        {
            var s = (vehicleStatus ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return "Ready to Deploy";
            if (s.Equals("OnTrip", OIC) || s.Equals("On Trip", OIC) || s.Equals("Active", OIC)) return "On Trip";
            if (s.Equals("Flagged", OIC)) return "Flagged";
            if (s.Equals("Pending", OIC)) return "Pending";
            if (s.Equals("Ready to Deploy", OIC) || s.Equals("Ready", OIC)) return "Ready to Deploy";
            return s;
        }

        // Remote camera control for administrators: any bus at any time, with no trip
        // required.
        //
        // These endpoints exist as a proxy because the service key bypasses row-level
        // security and must never reach the browser. The underlying tables and storage are
        // the same ones the driver app uses.

        private static readonly HttpClient _camHttp = new();

        private HttpRequestMessage CamReq(HttpMethod method, string path)
        {
            var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var url = config["Supabase:Url"];
            var key = config["Supabase:Key"];
            var req = new HttpRequestMessage(method, $"{url}/{path}");
            req.Headers.TryAddWithoutValidation("apikey", key);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            return req;
        }

        private async Task<System.Text.Json.JsonElement?> CamGetFirst(string path)
        {
            var res = await _camHttp.SendAsync(CamReq(HttpMethod.Get, path));
            if (!res.IsSuccessStatusCode) return null;
            var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var arr = doc.RootElement;
            return arr.GetArrayLength() > 0 ? arr[0].Clone() : null;
        }

        private async Task<bool> CamPatch(string deviceId, object body)
        {
            var req = CamReq(HttpMethod.Patch,
                $"rest/v1/device_config?device_id=eq.{Uri.EscapeDataString(deviceId)}");
            req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8, "application/json");
            var res = await _camHttp.SendAsync(req);
            return res.IsSuccessStatusCode;
        }

        /// <summary>Panel state: the device, its desired configuration, and what it
        /// reports.</summary>
        [HttpGet]
        public async Task<IActionResult> CameraState(string vehicleId)
        {
            var vResp = await _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Get();
            var dev = vResp.Models.FirstOrDefault()?.CounterDeviceId;
            if (string.IsNullOrEmpty(dev))
                return Json(new { deviceId = (string?)null });

            var esc = Uri.EscapeDataString(dev);
            var cfg = await CamGetFirst($"rest/v1/device_config?device_id=eq.{esc}");
            var st = await CamGetFirst($"rest/v1/device_status?device_id=eq.{esc}");
            return Json(new { deviceId = dev, config = cfg, status = st });
        }

        /// <summary>Asks the camera to wake and take a fresh photo of the doorway.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CameraWake(string deviceId)
        {
            var ok = await CamPatch(deviceId, new { wake_requested_at = DateTime.UtcNow });
            return ok ? Ok() : StatusCode(502);
        }

        /// <summary>
        /// Serves the camera's snapshot to the browser.
        /// </summary>
        /// <remarks>
        /// The storage bucket is private and the key stays on the server, so the image is
        /// proxied rather than linked. Caching is disabled because the object is
        /// overwritten in place on every wake, and a cached copy would show an earlier
        /// photo of the doorway.
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> CameraSnapshot(string deviceId)
        {
            var req = CamReq(HttpMethod.Get,
                $"storage/v1/object/authenticated/camera-snapshots/{Uri.EscapeDataString(deviceId)}.jpg");
            var res = await _camHttp.SendAsync(req);
            if (!res.IsSuccessStatusCode) return NotFound();
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Response.Headers.CacheControl = "no-store";
            return File(bytes, "image/jpeg");
        }

        /// <summary>
        /// Saves a calibration to the camera's configuration.
        /// </summary>
        /// <remarks>
        /// The version is re-read immediately before the write, so a concurrent editor,
        /// whether the driver app or the camera's own calibration screen, cannot take the
        /// same number with different content. The camera skips any version that is not
        /// strictly greater, so a collision would be silently ignored.
        ///
        /// The coordinates arrive as invariant-culture strings and are parsed as such.
        /// Form binding is culture-sensitive and would misread a decimal point under some
        /// locales.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CameraSave(
            string deviceId, string ax, string ay, string bx, string by,
            int inwardSign, bool useBack)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(ax, System.Globalization.NumberStyles.Float, inv, out var nAx) ||
                !double.TryParse(ay, System.Globalization.NumberStyles.Float, inv, out var nAy) ||
                !double.TryParse(bx, System.Globalization.NumberStyles.Float, inv, out var nBx) ||
                !double.TryParse(by, System.Globalization.NumberStyles.Float, inv, out var nBy))
                return BadRequest();

            var cfg = await CamGetFirst(
                $"rest/v1/device_config?device_id=eq.{Uri.EscapeDataString(deviceId)}&select=version");
            var curV = cfg?.TryGetProperty("version", out var v) == true ? v.GetInt32() : 0;
            var newV = curV + 1;

            var ok = await CamPatch(deviceId, new
            {
                line_ax = nAx,
                line_ay = nAy,
                line_bx = nBx,
                line_by = nBy,
                inward_sign = inwardSign,
                use_back_camera = useBack,
                version = newV,
                updated_by = "admin",
                updated_at = DateTime.UtcNow
            });

            // The counting line determines the passenger count, which determines the
            // revenue figure, so a change to it is worth attributing. The database trigger
            // records what changed; this records who changed it.
            //
            // Only a save is recorded. Requesting a fresh photo writes to a separate
            // column that the trigger ignores for the same reason.
            if (ok)
                await _audit.WriteAsync("camera_calibrated",
                    $"saved a new counting line for camera {deviceId} (v{newV})",
                    "device_config", deviceId);

            return ok ? Json(new { version = newV }) : StatusCode(502);
        }
    }
}
