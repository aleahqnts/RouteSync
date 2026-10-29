(function () {
    // BGC, Taguig — default center used until route bounds are available
    var DEFAULT_CENTER = [14.5508, 121.0509];
    var DEFAULT_ZOOM = 16;
    // The counter phone sends its figure every five seconds, so a poll on the same period
    // would add most of another one to the wait before a boarding shows here. The server
    // reuses the fleet, route and staff lists between reads, which is what keeps a poll
    // this frequent cheaper than the slower one it replaces.
    var POLL_INTERVAL_MS = 2000;


    // Distinct route colors, assigned deterministically by routeId so a route always
    // keeps the same color and any number of routes is supported (Route 1 = blue,
    // Route 2 = orange, …). Cycles if there are more routes than colors.
    var PALETTE = ['#2563EB', '#F97316', '#16A34A', '#DC2626', '#7C3AED', '#0891B2', '#DB2777', '#CA8A04'];

    // The ground the fleet covers, measured out from BGC in every direction. Past it
    // is open sea or country with no bus in it, and a map dragged out there takes a
    // Fit to buses to find the way back from.
    var REACH_LAT = 0.12;
    var REACH_LNG = 0.13;
    var REGION = L.latLngBounds(
        [DEFAULT_CENTER[0] - REACH_LAT, DEFAULT_CENTER[1] - REACH_LNG],
        [DEFAULT_CENTER[0] + REACH_LAT, DEFAULT_CENTER[1] + REACH_LNG]
    );

    var map = L.map('fleetMap', {
        maxBounds: REGION,
        // Held at the edge rather than sprung back from it, so a drag stops where
        // the region stops instead of bouncing off it.
        maxBoundsViscosity: 1
    });

    // OpenStreetMap's own servers are run by volunteers and refuse tiles to any web
    // page that does not say which site it is. The dashboard sends no Referer on
    // cross-origin requests at all, by its own Referrer-Policy header, so every tile
    // came back as an "Access blocked" image. The policy is overridden here for the
    // tiles alone, and only the origin is sent, never the page path.
    //
    // The one host without a subdomain, which OSM now asks for in place of a, b and c.
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        referrerPolicy: 'strict-origin-when-cross-origin',
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>',
        className: 'map-tiles'
    }).addTo(map);

    // window.fleetMapBounds = [south, west, north, east] computed from route waypoints;
    // falls back to the default center/zoom when no bounds are available
    if (window.fleetMapBounds) {
        var b = window.fleetMapBounds;
        var routeBounds = L.latLngBounds([b[0], b[1]], [b[2], b[3]]);
        REGION.extend(routeBounds);
        map.setMaxBounds(REGION);

        map.fitBounds(routeBounds, { padding: [60, 60], maxZoom: 16 });
    } else {
        map.setView(DEFAULT_CENTER, DEFAULT_ZOOM);
    }

    // Zooming out far enough is the other way of leaving the region, and bounds do
    // not stop it. The floor is whatever zoom holds the whole region on this screen,
    // measured rather than written down: a phone and a desk each reach it at their
    // own, and a window that changes size reaches a different one.
    function holdRegion() {
        map.setMaxBounds(REGION);
        map.setMinZoom(map.getBoundsZoom(REGION));
    }

    // Anything drawn on the map has to be reachable on it. The region is a courtesy
    // to the operator, not a statement about where the fleet may go, so whatever is
    // put on the map widens it: route lines, and the stops along them, which arrive
    // separately and are not in the bounds the page is given.
    function reachTo(points) {
        if (!points || !points.length) return;
        var before = REGION.toBBoxString();
        REGION.extend(L.latLngBounds(points));
        if (REGION.toBBoxString() !== before) holdRegion();
    }

    holdRegion();
    map.on('resize', holdRegion);

    // A zoom puts every marker somewhere new at the same moment. Letting them travel
    // there turns one gesture into a second of drift, so the gliding is off for the
    // length of it.
    //
    // The parked grids are laid out again for the new zoom before gliding comes back on,
    // a frame later, so they settle at once rather than sliding into place.
    map.on('zoomstart', function () { map.getContainer().classList.add('fm-map--jump'); });
    map.on('zoomend', function () {
        layoutParked();
        foldParked();
        requestAnimationFrame(function () { map.getContainer().classList.remove('fm-map--jump'); });
    });
    RouteMotion.watch(map);

    var routeColors = {};        // routeName -> color, built from the routes fetch
    var routePolylines = {};     // routeId -> [polylines]
    var routeLines = {};         // routeId -> measured line the markers travel along
    var rawLayer = L.layerGroup(); // raw GPS readings, drawn only while the switch is on
    var stopLayer = L.layerGroup().addTo(map);
    var busLayer = L.layerGroup().addTo(map);
    var terminalLayer = L.layerGroup().addTo(map); // terminal name labels
    var terminalSignature = null;                  // what those labels currently say

    // Parked buses are grouped per terminal and spread into a centred grid under the
    // terminal point the server sends, so the pills never overlap. The grid is measured
    // on the screen and laid out again on every zoom. Measured on the ground it sat pill to
    // pill zoomed out and spread across whole blocks zoomed in.
    var TERMINAL_PER_ROW = 4;
    var SLOT_W_PX = 88;       // a pill is 80 wide
    var SLOT_H_PX = 34;       // and 28 tall
    var LABEL_RISE_PX = 32;   // the terminal's name sits this far above the first row

    // A grid a fixed size on the screen is a city block at street level and half the
    // city zoomed out, where two terminals' grids run into each other and over the
    // routes. Below this zoom the parked buses fold into their terminal's label, which
    // keeps the count; clicking the label zooms in to them.
    var PARKED_MIN_ZOOM = 17;

    // Further out still, a terminal's full name covers the routes and the next terminal's,
    // so the label shrinks to the parking mark and its count, with the name on hover. A
    // phone has less room across, so there the full name waits for the parked grid.
    var NAMED_MIN_ZOOM = window.matchMedia('(max-width: 599px)').matches ? PARKED_MIN_ZOOM : 16;

    // A search is for one bus, so a parked bus that matches is shown at any zoom.
    function foldParked() {
        var z = map.getZoom(), box = map.getContainer();
        box.classList.toggle('fm-map--far', z < PARKED_MIN_ZOOM && !searchTerm);
        box.classList.toggle('fm-map--compact', z < NAMED_MIN_ZOOM);
    }
    var parkedSlots = {};     // vehicleId -> { lat, lng, i }: its terminal and place in the grid

    // Leaflet stacks markers by latitude, so a stop or parked pill just south of a bus
    // would be drawn over it. Buses on a trip sit above parked ones, and both above stops.
    function busZ(parked) { return parked ? 10000 : 20000; }

    // The point this many screen pixels from another at the current zoom.
    function offsetPx(lat, lng, dx, dy) {
        var z = map.getZoom();
        var p = map.project([lat, lng], z);
        return map.unproject([p.x + dx, p.y + dy], z);
    }

    // The grid's first row sits this far below the terminal point, leaving the point
    // itself to a bus on a trip standing there, with the terminal's name in between.
    var GRID_DROP_PX = 72;
    var gridShifts = {};      // terminal, zoom and bus count -> sideways shift of its grid

    // How far sideways a terminal's grid sits: centred under the terminal or off to
    // either side, whichever covers the fewest points along the route lines.
    function gridShift(lat, lng, count) {
        var z = map.getZoom();
        var key = lat + ',' + lng + '@' + z + '#' + count;
        if (key in gridShifts) return gridShifts[key];

        var o = map.project([lat, lng], z);
        var w = TERMINAL_PER_ROW * SLOT_W_PX;
        var side = w / 2 + SLOT_W_PX / 2;
        var top = o.y + GRID_DROP_PX - LABEL_RISE_PX - 13;
        var bottom = o.y + GRID_DROP_PX + (Math.ceil(count / TERMINAL_PER_ROW) - 0.5) * SLOT_H_PX;

        // Points every 8 px along every route line, kept only near the terminal.
        var pts = [];
        Object.keys(routePolylines).forEach(function (rid) {
            routePolylines[rid].forEach(function (line) {
                var prev = null;
                line.getLatLngs().forEach(function (ll) {
                    var p = map.project(ll, z);
                    if (prev) {
                        var steps = Math.max(1, Math.ceil(prev.distanceTo(p) / 8));
                        for (var s = 1; s <= steps; s++) {
                            var x = prev.x + (p.x - prev.x) * s / steps, y = prev.y + (p.y - prev.y) * s / steps;
                            if (y > top && y < bottom && Math.abs(x - o.x) < side + w / 2) pts.push(x);
                        }
                    }
                    prev = p;
                });
            });
        });

        var best = 0, fewest = Infinity;
        [0, -side, side].forEach(function (dx) {
            var left = o.x + dx - w / 2, right = left + w;
            var n = pts.filter(function (x) { return x > left && x < right; }).length;
            if (n < fewest) { fewest = n; best = dx; }
        });
        return (gridShifts[key] = best);
    }

    function terminalSlot(lat, lng, i, count) {
        var row = Math.floor(i / TERMINAL_PER_ROW);
        var col = i % TERMINAL_PER_ROW;
        return offsetPx(lat, lng, gridShift(lat, lng, count) + (col - (TERMINAL_PER_ROW - 1) / 2) * SLOT_W_PX,
            GRID_DROP_PX + row * SLOT_H_PX);
    }

    // Above its grid while the grid shows, and just above the terminal point once the
    // grid folds into it.
    function terminalLabelPos(label) {
        var a = label._anchor;
        return map.getZoom() < PARKED_MIN_ZOOM
            ? offsetPx(a[0], a[1], 0, -LABEL_RISE_PX)
            : offsetPx(a[0], a[1], gridShift(a[0], a[1], label._count), GRID_DROP_PX - LABEL_RISE_PX);
    }

    // Puts every parked bus and terminal label back in its grid for the current zoom.
    function layoutParked() {
        Object.keys(parkedSlots).forEach(function (id) {
            var slot = parkedSlots[id];
            var marker = busMarkers[id];
            if (marker) marker.setLatLng(terminalSlot(slot.lat, slot.lng, slot.i, slot.n));
        });
        terminalLayer.eachLayer(function (label) {
            label.setLatLng(terminalLabelPos(label));
        });
    }

    // vehicleId -> live Leaflet marker; markers are moved in place between polls,
    // never recreated, so open tooltips don't flicker.
    var busMarkers = {};

    var routeSelect = document.getElementById('fmRouteFilter');
    var statusSelect = document.getElementById('fmStatusFilter');
    var searchInput = document.getElementById('fmSearch');
    var clearBtn = document.getElementById('fmClearFilters');
    var countEl = document.getElementById('fmCount');
    var fitBtn = document.getElementById('fmFitBtn');
    var rawBtn = document.getElementById('fmRawBtn');
    var gpsDrawer = document.getElementById('fmGpsDrawer');
    var gpsToggle = document.getElementById('fmGpsToggle');
    var gpsBody = document.getElementById('fmGpsBody');
    var connBadge = document.getElementById('fmConnBadge');
    var legendEl = document.getElementById('fmLegend');
    var legendToggle = document.getElementById('fmLegendToggle');

    if (legendToggle && legendEl && window.matchMedia('(hover: none) and (pointer: coarse)').matches) {
        legendToggle.setAttribute('aria-expanded', 'false');
        legendEl.hidden = true;
    }

    // The GPS tools drawer folds away to its heading, as the legend does.
    if (gpsToggle && gpsBody) {
        gpsToggle.addEventListener('click', function () {
            var open = gpsToggle.getAttribute('aria-expanded') === 'true';
            gpsToggle.setAttribute('aria-expanded', open ? 'false' : 'true');
            gpsBody.hidden = open;
        });
    }

    if (legendToggle && legendEl) {
        legendToggle.addEventListener('click', function () {
            var open = legendToggle.getAttribute('aria-expanded') === 'true';
            legendToggle.setAttribute('aria-expanded', open ? 'false' : 'true');
            legendEl.hidden = open;
        });
    }

    // Side panel — tracks which bus is open so each poll refreshes it live.
    var panel = document.getElementById('fmPanel');
    var panelClose = document.getElementById('fmPanelClose');
    var selectedVehicleId = null;

    var searchTerm = '';
    var pesoFmt = new Intl.NumberFormat('en-PH', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

    function colorForRouteId(routeId) {
        var i = ((routeId - 1) % PALETTE.length + PALETTE.length) % PALETTE.length;
        return PALETTE[i];
    }

    function colorForRoute(routeName) {
        return routeColors[routeName] || '#666';
    }

    function statusColor(status) {
        if (status === 'On Trip') return '#34C759'; // green — moving / on trip
        if (status === 'Flagged') return '#DC2626'; // red
        if (status === 'Out of Service') return '#6B7280'; // slate — grounded
        if (status === 'Offline') return '#9CA3AF'; // grey
        if (status === 'Pending') return '#F59E0B'; // amber, waiting on its checklist, as on Dispatch
        return '#3B82F6'; // blue, ready to deploy and other parked, as on Dispatch
    }

    // Server timestamps are UTC but serialized without a 'Z', so append one before parsing.
    function relativeTime(ts) {
        var then = new Date(ts + 'Z').getTime();
        if (isNaN(then)) return '';
        var secs = Math.max(0, Math.round((Date.now() - then) / 1000));
        if (secs === 1) return '1 second ago';
        if (secs < 60) return secs + ' seconds ago';
        var mins = Math.round(secs / 60);
        if (mins === 1) return '1 minute ago';
        if (mins < 60) return mins + ' minutes ago';
        var hrs = Math.round(mins / 60);
        return hrs === 1 ? '1 hour ago' : hrs + ' hours ago';
    }

    // The moment itself, on the Philippine clock to the minute, whatever zone the
    // browser is in. Seconds would tick on every poll and say nothing more. The date is
    // said too once it is not today's.
    var phTimeFmt = new Intl.DateTimeFormat('en-PH', {
        timeZone: 'Asia/Manila', hour: 'numeric', minute: '2-digit', hour12: true
    });
    var phDateFmt = new Intl.DateTimeFormat('en-PH', {
        timeZone: 'Asia/Manila', month: 'short', day: 'numeric', year: 'numeric'
    });
    function phClock(ts) {
        var then = new Date(ts + 'Z');
        if (isNaN(then.getTime())) return '';
        var day = phDateFmt.format(then);
        var time = phTimeFmt.format(then);
        return day === phDateFmt.format(new Date()) ? time : day + ', ' + time;
    }

    // A bus on its driver's break is still on its trip: it keeps reporting, keeps its
    // color and still counts as out. Only what it is called changes.
    function statusText(bus) {
        return bus.onBreakUntil ? 'On Break until ' + bus.onBreakUntil : bus.status;
    }

    // The time to the next stop as a rough figure, never a clock time: it is an estimate
    // from how fast buses usually cover that stretch. Counted from the reading it was
    // worked out at, so it keeps falling between readings.
    var ETA_ARRIVING_SECONDS = 10;

    // What the panel last showed. Each reading brings a fresh estimate, and the figure
    // eases toward it, a fifth of the gap each second, rather than jumping to it. A
    // slightly later estimate is the estimate's own wobble and holds the figure still
    // until the two meet; one more than ETA_RISE_SECONDS later is the bus genuinely
    // running late, and the figure climbs to it.
    var ETA_RISE_SECONDS = 30;
    var etaShown = null;

    function smoothLeft(bus, target) {
        var now = Date.now();
        var s = etaShown;
        var left = target;
        if (s && s.bus === bus.vehicleId && s.next === bus.nextStop && now - s.at < 10000) {
            var dt = (now - s.at) / 1000;
            left = s.left - dt;
            var eased = left + (target - left) * (1 - Math.pow(0.8, dt));
            left = target - left > ETA_RISE_SECONDS ? eased : Math.min(s.left, eased);
        }
        etaShown = { bus: bus.vehicleId, next: bus.nextStop, left: left, at: now };
        return left;
    }

    function etaText(bus) {
        if (isStale(bus)) return 'No recent signal';
        if (bus.atStop) return 'At ' + bus.atStop;
        if (!bus.nextStop) return bus.onRoute ? 'Past the last stop' : 'No estimate right now';
        if (bus.nextStopSeconds == null) return '';
        var left = smoothLeft(bus, bus.nextStopSeconds - readingAge(bus) / 1000);
        // The last few seconds are within the estimate's own error, so they are not counted.
        if (left <= ETA_ARRIVING_SECONDS) return 'Arriving';
        // Seconds only in the last minute; further out, whole minutes.
        var s = Math.round(left);
        return 'About ' + (s > 60 ? Math.round(s / 60) + ' min' : s + ' sec');
    }

    // The day sheet of the parked bus shown, read when the panel opens on it and again
    // once it is a minute old.
    var dayFor = null, dayAt = 0;

    function loadDay(vehicleId) {
        dayFor = vehicleId;
        dayAt = Date.now();
        var trips = document.getElementById('fmPanelTrips');
        if (!trips.children.length) trips.innerHTML = '<li class="fm-panel__muted">Loading\u2026</li>';
        fetch('/FleetMap/BusDetail?vehicleId=' + encodeURIComponent(vehicleId))
            .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
            .then(function (d) {
                if (selectedVehicleId !== vehicleId) return;
                var seats = document.getElementById('fmPanelSeats');
                seats.textContent = d.seats + ' seats';
                seats.hidden = !d.seats;

                trips.innerHTML = d.trips.length
                    ? d.trips.map(function (t) {
                        return '<li><span class="fm-panel__trip-shift">' + escapeHtml(t.shift) + '</span>' +
                            '<span class="fm-panel__trip-end">' + escapeHtml(t.status) + '</span>' +
                            '<span class="fm-panel__muted">' + escapeHtml(t.window) + '</span>' +
                            '<span class="fm-panel__muted fm-panel__trip-end">' +
                            (t.boarded ? t.boarded + ' boarded' : '') + '</span></li>';
                    }).join('')
                    : '<li class="fm-panel__muted">No trips today</li>';

                var insp = document.getElementById('fmPanelInspection');
                insp.innerHTML = d.inspection
                    ? escapeHtml(d.inspection.result) + '<span class="fm-panel__sub">' + escapeHtml(d.inspection.at) + '</span>'
                    : 'None on record';
                insp.classList.toggle('fm-panel__bad', !!d.inspection && d.inspection.result === 'Failed');

                // The faults listed, or the open orders where none names its faults.
                var count = document.getElementById('fmPanelFaultCount');
                count.textContent = d.faults.length || d.faultCount || 'None';
                count.classList.toggle('fm-panel__bad', d.faultCount > 0);
                var shown = d.faults.slice(0, 3);
                document.getElementById('fmPanelFaults').innerHTML = shown.map(function (f) {
                    return '<li>' + escapeHtml(f) + '</li>';
                }).join('') + (d.faults.length > 3
                    ? '<li class="fm-panel__muted">and ' + (d.faults.length - 3) + ' more</li>' : '');
            })
            .catch(function () {
                if (selectedVehicleId !== vehicleId) return;
                dayFor = null;
                trips.innerHTML = '<li class="fm-panel__muted">Could not load this bus\u2019s day.</li>';
            });
    }

    // Fills the line between the two stops up to the bus. It glides between readings,
    // but jumps when the bus passes a stop and the line starts over, or when the panel
    // turns to another bus, rather than sweeping back across.
    var legShown = { bus: null, at: 0 };

    function placeOnLeg(progress, vehicleId) {
        var leg = document.getElementById('fmPanelLeg');
        var at = progress == null ? 0 : progress;
        var jump = vehicleId !== legShown.bus || at < legShown.at - 0.01;
        leg.classList.toggle('fm-panel__leg--placed', progress != null);
        leg.classList.toggle('fm-panel__leg--at-stop', progress != null && (at <= 0 || at >= 1));
        if (jump) leg.classList.add('fm-panel__leg--jump');
        leg.style.setProperty('--fm-leg', at.toFixed(3));
        if (jump) {
            void leg.offsetWidth;
            leg.classList.remove('fm-panel__leg--jump');
        }
        legShown = { bus: vehicleId, at: at };
    }

    // How far along the stretch the bus is, from where the map draws it this moment rather
    // than from its last reading, so the strip moves with the pin instead of stepping at
    // each reading. Inside the next stop's zone it is there; standing, it is at the start.
    var ZONE_BEFORE_M = 30; // StopEta.ZoneBefore

    function legProgress(bus) {
        if (bus.nextStopProgress == null) return null;
        if (bus.atStop) return 0;
        var m = busMarkers[bus.vehicleId];
        if (!m || !m._rmLine || m._rmAlong == null || bus.nextStopAlong == null || !bus.nextStopSpan)
            return bus.nextStopProgress;
        var left = m._rmLine.ahead(m._rmAlong, bus.nextStopAlong);
        if (left <= ZONE_BEFORE_M) return 1;
        return Math.max(0, Math.min(1, 1 - left / bus.nextStopSpan));
    }

    function fillPanel(bus) {
        var live = !!bus.tripId;
        document.getElementById('fmPanelBus').textContent = bus.vehicleId;
        document.getElementById('fmPanelPlate').textContent = bus.plateNumber;
        document.getElementById('fmPanelRoute').textContent = String(bus.routeId).padStart(2, '0');
        document.getElementById('fmPanelShift').textContent = bus.shift;
        document.getElementById('fmPanelShift').hidden = !live;
        document.getElementById('fmPanelStatus').textContent = statusText(bus);
        document.getElementById('fmPanelStatusDot').style.background = statusColor(bus.status);
        document.getElementById('fmPanelLive').hidden = !live;
        document.getElementById('fmPanelDay').hidden = live;

        if (live) {
            document.getElementById('fmPanelSeats').hidden = true;
            var known = bus.onRoute && !isStale(bus);
            document.getElementById('fmPanelPrev').textContent = known ? (bus.previousStop || 'None yet') : 'Not known';
            document.getElementById('fmPanelNext').textContent = known ? (bus.nextStop || 'None') : 'Not known';
            document.getElementById('fmPanelEta').textContent = etaText(bus);
            // Standing at a stop, the bus sits on the ring of the stop it is at.
            placeOnLeg(known ? legProgress(bus) : null, bus.vehicleId);
            document.getElementById('fmPanelDriver').textContent = bus.driverName;
            document.getElementById('fmPanelPax').textContent = bus.passengers;
            // Written as an escape rather than the sign itself: this file carries no
            // byte order mark, so it is decoded as whatever the response says, and a
            // peso sign is the one character here that would not survive being read as
            // anything but UTF-8.
            document.getElementById('fmPanelRevenue').textContent = '\u20B1' + pesoFmt.format(bus.estimatedRevenue);
            var updated = document.getElementById('fmPanelUpdated');
            updated.textContent = relativeTime(bus.timestamp);
            updated.title = phClock(bus.timestamp);
        } else if (dayFor !== bus.vehicleId || Date.now() - dayAt > 60000) {
            if (dayFor !== bus.vehicleId) {
                document.getElementById('fmPanelTrips').innerHTML = '';
                document.getElementById('fmPanelFaults').innerHTML = '';
                document.getElementById('fmPanelInspection').textContent = '';
                document.getElementById('fmPanelFaultCount').textContent = '';
                document.getElementById('fmPanelSeats').hidden = true;
            }
            loadDay(bus.vehicleId);
        }

        var actions = document.getElementById('fmPanelActions');
        actions.hidden = !bus.tripId;
        if (bus.tripId) {
            var base = '/Dispatch?trip=' + encodeURIComponent(bus.tripId) + '&do=';
            document.getElementById('fmActView').href = base + 'view';
            document.getElementById('fmActReassign').href = base + 'reassign';
            document.getElementById('fmActMessage').href = base + 'message';
        }
    }

    var panelReturnFocus = null;

    function openPanel(vehicleId) {
        if (!panel.classList.contains('fm-panel--open')) panelReturnFocus = document.activeElement;
        selectedVehicleId = vehicleId;
        var marker = busMarkers[vehicleId];
        if (marker && marker._bus) fillPanel(marker._bus);
        panel.classList.add('fm-panel--open');
        panel.setAttribute('aria-hidden', 'false');
        panelClose.focus();
    }

    function closePanel() {
        selectedVehicleId = null;
        dayFor = null;
        panel.classList.remove('fm-panel--open');
        panel.setAttribute('aria-hidden', 'true');
        if (panelReturnFocus && document.contains(panelReturnFocus)) panelReturnFocus.focus();
        panelReturnFocus = null;
    }

    panelClose.addEventListener('click', closePanel);
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && panel.classList.contains('fm-panel--open')) closePanel();
    });

    function escapeHtml(v) {
        return String(v == null ? '' : v).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    // A reading older than this is where the bus was, not where it is. A phone on a trip
    // reports at least every thirty seconds even standing still, so this is three missed
    // in a row. Only a trip reports; a bus standing at its terminal is not reporting and is
    // not late for it, and the timestamp it is given is the moment the board was drawn.
    var STALE_AFTER_MS = 90 * 1000;

    function readingAge(bus) {
        var then = new Date(bus.timestamp + 'Z').getTime();
        return isNaN(then) ? 0 : Math.max(0, Date.now() - then);
    }

    function isStale(bus) {
        return bus.status === 'On Trip' && readingAge(bus) >= STALE_AFTER_MS;
    }

    // A bus off its route is drawn with a dashed edge: it is where the phone is, not on
    // the road the route runs along, and should not be read as keeping to it.
    function busIcon(label, color, stale, off, parked) {
        return L.divIcon({
            className: 'fm-bus-marker' + (stale ? ' fm-bus-marker--stale' : '') + (off ? ' fm-bus-marker--off' : '')
                + (parked ? ' fm-bus-marker--parked' : ''),
            html: '<span style="background:' + color + '">' + RouteMotion.arrowHtml + escapeHtml(label)
                + '<b class="fm-bus-count"></b></span>',
            iconSize: [80, 28],
            iconAnchor: [40, 14]
        });
    }

    // Straight-line gliding, for a bus drawn where its phone is. A bus on its route is
    // driven along the road frame by frame instead, and a CSS transition on top of that
    // would drag every frame behind the last.
    //
    // Turned on a frame after the marker exists. On at once, its first position would be
    // animated from the corner of the map: the element is in the page before Leaflet
    // gives it a place.
    function setGlide(marker, on) {
        requestAnimationFrame(function () {
            var el = marker.getElement();
            if (el) el.classList.toggle('fm-bus-marker--glide', on);
        });
    }

    // Where a bus is drawn, and how it gets there. On its route it travels the road from
    // the last reading to this one; otherwise it is placed at the position the server gave.
    function moveBus(marker, bus, pos) {
        var line = routeLines[bus.routeId];
        var onRoad = bus.status === 'On Trip' && bus.onRoute && bus.along != null && line;

        if (onRoad) {
            setGlide(marker, false);
            RouteMotion.drive(marker, map, line, bus.along, bus.bearing != null, bus.timestamp, bus.speed,
                bus.atStopAlong);
        } else {
            RouteMotion.release(marker);
            setGlide(marker, true);
            marker.setLatLng(pos);
            RouteMotion.point(marker, bus.status === 'On Trip' ? bus.bearing : null);
        }
        return !!onRoad;
    }

    // Buses on their route are laid out together, every frame, from where the motion
    // script has each one on its line. Buses that would cover each other on the screen,
    // going the same way, queue one pill behind another along their own route lines, in
    // the order they came together: at a shared stop the first to arrive sits on the stop.
    // A moving bus keeps a few pixels to the right of the road, as traffic does here, so
    // buses passing the other way on a two-way road do not cover each other.
    //
    // Zoomed out, where a pill is a block or more of road, buses that would cover each
    // other are drawn as one pill with their count, at the front bus, which zooms in to
    // them when clicked. Each bus keeps moving at every zoom; only the drawing changes.
    //
    // Only the buses shown count, so the route and status filters and the search lay the
    // queues out again without the buses they hide.
    var KEEP_RIGHT_PX = 6;
    var SAME_WAY_DEG = 60;
    map._rmLaysOut = true;

    function angleApart(a, b) {
        return Math.abs(((a - b) % 360 + 540) % 360 - 180);
    }

    function metresPerPixel(lat) {
        return 40075016.686 * Math.cos(lat * Math.PI / 180) / Math.pow(2, map.getZoom() + 8);
    }

    function layoutRoad() {
        var merging = map.getZoom() < NAMED_MIN_ZOOM;
        var items = [];
        Object.keys(busMarkers).forEach(function (id) {
            var m = busMarkers[id];
            if (!m._rmLine || m._rmBase == null || !busLayer.hasLayer(m)) return;
            var ll = L.latLng(m._rmLine.pointAt(m._rmBase));
            items.push({
                id: id, m: m, ll: ll, pt: map.latLngToLayerPoint(ll),
                bearing: m._rmLine.bearingAt(m._rmBase),
                standingSince: m._bus && m._bus.atStop ? m._bus.atStopSince : null
            });
        });

        // Buses whose pills would touch, going the same way, or any way when zoomed out.
        var root = items.map(function (_, i) { return i; });
        function find(i) { while (root[i] !== i) i = root[i] = root[root[i]]; return i; }
        for (var i = 0; i < items.length; i++) {
            for (var j = i + 1; j < items.length; j++) {
                if (items[i].pt.distanceTo(items[j].pt) < SLOT_W_PX
                    && (merging || angleApart(items[i].bearing, items[j].bearing) <= SAME_WAY_DEG))
                    root[find(j)] = find(i);
            }
        }
        var groups = {};
        items.forEach(function (it, i) { (groups[find(i)] = groups[find(i)] || []).push(it); });

        var now = Date.now();
        Object.keys(groups).forEach(function (key) {
            var group = groups[key];

            // The order is set when a bus joins and kept while the group lasts, so a bus
            // drawn behind another is never drawn back past it. Buses joining together go
            // in the order they reached the stop, then front to back along the road.
            if (group.length > 1) {
                var joining = group.filter(function (it) { return it.m._rmJoined == null; });
                joining.sort(function (a, b) {
                    if (a.standingSince && b.standingSince && a.standingSince !== b.standingSince)
                        return a.standingSince < b.standingSince ? -1 : 1;
                    if (!!a.standingSince !== !!b.standingSince) return a.standingSince ? -1 : 1;
                    var r = a.bearing * Math.PI / 180;
                    var ahead = (b.pt.x - a.pt.x) * Math.sin(r) - (b.pt.y - a.pt.y) * Math.cos(r);
                    return ahead > 0 ? 1 : -1;
                });
                joining.forEach(function (it, k) { it.m._rmJoined = now + k / 1000; });
                group.sort(function (a, b) { return a.m._rmJoined - b.m._rmJoined; });
            } else {
                group[0].m._rmJoined = null;
            }

            var routes = {};
            group.forEach(function (it) { routes[it.m._bus ? it.m._bus.routeId : ''] = true; });
            var mixed = Object.keys(routes).length > 1;

            group.forEach(function (it, rank) {
                var m = it.m, el = m.getElement();
                var lead = rank === 0;
                var ll = it.ll;

                // Behind the front bus by a pill per place in the queue, counting what the
                // bus is already behind it, so a bus a pill or more back is not moved.
                if (!merging && rank > 0) {
                    var front = group[0], d = it.bearing * Math.PI / 180;
                    var behindPx = (front.pt.x - it.pt.x) * Math.sin(d) - (front.pt.y - it.pt.y) * Math.cos(d);
                    var backPx = Math.max(0, rank * SLOT_W_PX - behindPx);
                    if (backPx > 0) ll = L.latLng(m._rmLine.pointAt(m._rmBase - backPx * metresPerPixel(it.ll.lat)));
                }
                if (!merging && m._rmMoving && !it.standingSince) {
                    var r = m._rmLine.bearingAt(m._rmBase) * Math.PI / 180;
                    var p = map.latLngToLayerPoint(ll);
                    ll = map.layerPointToLatLng([p.x + KEEP_RIGHT_PX * Math.cos(r), p.y + KEEP_RIGHT_PX * Math.sin(r)]);
                }

                var size = merging && lead ? group.length : 1;
                if (el) {
                    el.classList.toggle('fm-bus-marker--tucked', merging && !lead);
                    el.classList.toggle('fm-bus-marker--mixed', size > 1 && mixed);
                    var count = el.querySelector('.fm-bus-count');
                    var text = size > 1 ? String(size) : '';
                    if (count && count.textContent !== text) count.textContent = text;
                }
                if (m._rmGroupSize !== size) {
                    m._rmGroupSize = size;
                    m.setTooltipContent(size > 1
                        ? '<div class="fm-tooltip"><div class="fm-tooltip__header"><span class="fm-tooltip__bus">'
                            + size + ' buses</span></div>' + group.map(function (g) {
                                return '<div class="fm-tooltip__plate">' + escapeHtml(g.id) + ' &middot; '
                                    + escapeHtml(g.m._bus ? g.m._bus.routeName : '') + '</div>';
                            }).join('') + '</div>'
                        : tooltipHtml(m._bus));
                }

                var was = m.getLatLng();
                if (!was || Math.abs(was.lat - ll.lat) > 1e-8 || Math.abs(was.lng - ll.lng) > 1e-8)
                    m.setLatLng(ll);
            });
        });
    }

    // Asked for again before laying out, so one frame that fails does not stop the rest.
    (function layoutLoop() {
        requestAnimationFrame(layoutLoop);
        if (!map._rmZooming) layoutRoad();
    })();

    function tooltipHtml(bus) {
        var sc = statusColor(bus.status);
        return '<div class="fm-tooltip">' +
                '<div class="fm-tooltip__header">' +
                    '<span class="fm-tooltip__bus">' + escapeHtml(bus.vehicleId) + '</span>' +
                    '<span class="fm-tooltip__route">' + escapeHtml(bus.routeName) + '</span>' +
                '</div>' +
                '<div class="fm-tooltip__plate">' + escapeHtml(bus.plateNumber) + '</div>' +
                (bus.atStop && !isStale(bus)
                    ? '<div class="fm-tooltip__stop">At ' + escapeHtml(bus.atStop) + '</div>'
                    : '') +
                '<div class="fm-tooltip__status" style="color:' + sc + '"><span class="fm-tooltip__dot" style="background:' + sc + '"></span>' + escapeHtml(statusText(bus)) + '</div>' +
                '<div class="fm-tooltip__passengers"><span>Total Passengers</span><strong>' + escapeHtml(bus.passengers) + '</strong></div>' +
                (isStale(bus)
                    ? '<div class="fm-tooltip__stale">Last heard from ' + escapeHtml(relativeTime(bus.timestamp)) + '</div>'
                    : '') +
            '</div>';
    }

    function setConnectionLost(lost) {
        if (connBadge) connBadge.classList.toggle('fm-conn-badge--visible', lost);
    }

    // Add a terminal name label above its parked-bus grid.
    function addTerminalLabel(lat, lng, name, count) {
        var safe = escapeHtml(name || 'Terminal');
        var html = '<div class="fm-terminal-pill" title="' + safe + ' · ' + count + ' parked">'
                 + '<span class="fm-terminal-p" aria-hidden="true">P</span>'
                 + '<span class="fm-terminal-name">' + safe + ' · </span>' + count + '</div>';
        var label = L.marker([lat, lng], {
            icon: L.divIcon({ className: 'fm-terminal-label', html: html, iconSize: [200, 26], iconAnchor: [100, 13] }),
            zIndexOffset: -500
        });
        label._anchor = [lat, lng];
        label._count = count;
        label.setLatLng(terminalLabelPos(label));
        label.on('click', function () {
            map.setView(this._anchor, Math.max(map.getZoom(), PARKED_MIN_ZOOM));
        });
        label.addTo(terminalLayer);
    }

    // Does a bus match the current search term? (vehicle id, plate, driver, or route)
    function matchesSearch(bus) {
        if (!searchTerm) return true;
        return [bus.vehicleId, bus.plateNumber, bus.driverName, bus.routeName]
            .some(function (v) { return v && String(v).toLowerCase().indexOf(searchTerm) !== -1; });
    }

    // Show/hide bus markers to match the search box, without recreating them.
    function applySearch() {
        Object.keys(busMarkers).forEach(function (id) {
            var marker = busMarkers[id];
            var show = marker._bus ? matchesSearch(marker._bus) : true;
            if (show && !busLayer.hasLayer(marker)) busLayer.addLayer(marker);
            else if (!show && busLayer.hasLayer(marker)) busLayer.removeLayer(marker);
        });
        updateTally();
    }

    // Counts what is actually on the map, so the tally and the empty state always agree
    // with what the operator can see, whichever filter hid the rest.
    function updateTally() {
        var shown = 0, moving = 0;
        Object.keys(busMarkers).forEach(function (id) {
            var marker = busMarkers[id];
            if (!busLayer.hasLayer(marker)) return;
            shown++;
            var st = marker._bus && marker._bus.status;
            if (st === 'On Trip' || st === 'Active') moving++;
        });

        // Two pills: the buses shown, and how many of them are on a trip, whose dot pulses
        // while any are moving.
        if (countEl) {
            countEl.innerHTML = shown === 0
                ? ''
                : '<span class="fm-chip"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" '
                    + 'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="4" y="3" width="16" height="14" rx="3"/>'
                    + '<path d="M4 11h16M8 21v-4M16 21v-4"/></svg>' + shown + (shown === 1 ? ' bus' : ' buses') + '</span>'
                + '<span class="fm-chip fm-chip--trip' + (moving > 0 ? ' fm-chip--live' : '') + '">'
                    + '<span class="fm-chip-dot"></span>' + moving + ' on trip</span>';
        }
        if (fitBtn) fitBtn.disabled = shown === 0;
    }

    // Poll the live Positions endpoint, honouring the current Route/Status filters.
    var askedBus = new URLSearchParams(location.search).get('bus');

    // One request out at a time. On a weak signal an answer can take longer than the poll
    // interval; overlapping requests would queue behind each other and a late, older answer
    // could move a bus backwards. A call made while one is out (a filter change) runs once
    // the current answer is in, so it is never lost. A request still out after 8 s is given up.
    var positionsBusy = false, positionsAgain = false;

    function fetchPositions() {
        if (positionsBusy) { positionsAgain = true; return; }
        positionsBusy = true;
        // A search is a request for one specific bus, so it looks across the whole fleet.
        // Leaving the dropdowns applied meant a plate could not be found while a route was
        // selected, which reads as the search being broken rather than filtered.
        var searching = !!searchTerm;
        var routeId = searching ? null : (parseInt(routeSelect.value) || null);
        var status = searching ? null : (statusSelect.value || null);

        var params = [];
        if (routeId) params.push('routeId=' + routeId);
        if (status) params.push('status=' + encodeURIComponent(status));
        var url = '/FleetMap/Positions' + (params.length ? '?' + params.join('&') : '');

        fetch(url, { signal: AbortSignal.timeout(8000) })
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (buses) {
                setConnectionLost(false);

                // Parked (non-Active) buses: group by terminal, then lay each terminal's
                // buses out in a stable grid (sorted by id so slots don't reshuffle).
                var parkedGroups = {};
                buses.forEach(function (b) {
                    if (b.status === 'On Trip') return;
                    var key = b.terminalName || (b.lat + ',' + b.lng);
                    if (!parkedGroups[key]) parkedGroups[key] = { name: b.terminalName, lat: b.lat, lng: b.lng, list: [] };
                    parkedGroups[key].list.push(b);
                });

                var parkedPos = {};
                parkedSlots = {};
                var keys = Object.keys(parkedGroups).sort();
                var signature = keys.map(function (key) {
                    var g = parkedGroups[key];
                    return key + ':' + g.name + ':' + g.list.length;
                }).join('|');

                keys.forEach(function (key) {
                    var g = parkedGroups[key];
                    g.list.sort(function (a, b) { return a.vehicleId < b.vehicleId ? -1 : 1; });
                    g.list.forEach(function (b, i) {
                        parkedSlots[b.vehicleId] = { lat: g.lat, lng: g.lng, i: i, n: g.list.length };
                        parkedPos[b.vehicleId] = terminalSlot(g.lat, g.lng, i, g.list.length);
                    });
                });

                // The labels say a terminal name and a count, and neither changes from
                // one poll to the next. Rebuilding them every five seconds threw away
                // and remade every one of them for nothing.
                if (signature !== terminalSignature) {
                    terminalSignature = signature;
                    terminalLayer.clearLayers();
                    keys.forEach(function (key) {
                        var g = parkedGroups[key];
                        addTerminalLabel(g.lat, g.lng, g.name, g.list.length);
                    });
                }

                var seen = {};
                buses.forEach(function (bus) {
                    seen[bus.vehicleId] = true;
                    var color = colorForRoute(bus.routeName);
                    var pos = parkedPos[bus.vehicleId] || [bus.lat, bus.lng];
                    var marker = busMarkers[bus.vehicleId];

                    var stale = isStale(bus);
                    var off = bus.status === 'On Trip' && bus.offRoute;
                    var parked = !!parkedPos[bus.vehicleId];
                    var iconKey = color + (stale ? '|stale' : '') + (off ? '|off' : '') + (parked ? '|parked' : '');

                    if (marker) {
                        // Moved in place, and left alone otherwise. Setting the icon
                        // replaces the element it is drawn in, which throws away the
                        // travel between readings and any tooltip open on it, so it is
                        // done only when what the icon shows has changed.
                        if (marker._iconKey !== iconKey) {
                            marker.setIcon(busIcon(bus.vehicleId, color, stale, off, parked));
                            marker.setZIndexOffset(busZ(parked));
                            marker._iconKey = iconKey;
                            RouteMotion.repoint(marker);
                        }
                        moveBus(marker, bus, pos);
                        if (!(marker._rmGroupSize > 1)) marker.setTooltipContent(tooltipHtml(bus));
                    } else {
                        marker = L.marker(pos, { icon: busIcon(bus.vehicleId, color, stale, off, parked), zIndexOffset: busZ(parked) })
                            .bindTooltip(tooltipHtml(bus), { direction: 'top', offset: [0, -10], className: 'fm-tooltip-wrap', opacity: 1 })
                            .addTo(busLayer);
                        marker.on('click', function () {
                            // A pill standing for several buses zoomed out opens onto them.
                            if (this._rmGroupSize > 1) {
                                map.setView(this.getLatLng(), Math.max(map.getZoom() + 2, NAMED_MIN_ZOOM));
                                return;
                            }
                            openPanel(this._bus.vehicleId);
                        });
                        marker._iconKey = iconKey;
                        busMarkers[bus.vehicleId] = marker;
                        moveBus(marker, bus, pos);
                    }
                    marker._bus = bus; // keep latest data for tooltip/panel refresh
                    // Standing at a stop, the pill covers the stop's dot, so it wears a ring
                    // in the stop's colour instead.
                    var el = marker.getElement();
                    if (el) el.classList.toggle('fm-bus-marker--at-stop', !!bus.atStop && !stale);
                });

                // Drop buses that fell out of the response (trip ended or filtered out).
                Object.keys(busMarkers).forEach(function (id) {
                    if (!seen[id]) {
                        RouteMotion.release(busMarkers[id]);
                        busLayer.removeLayer(busMarkers[id]);
                        delete busMarkers[id];
                    }
                });

                applySearch();
                drawRaw();
                foldParked();

                // Opened from the needs-attention list: centre on that bus and show it, once.
                if (askedBus && busMarkers[askedBus]) {
                    map.setView(busMarkers[askedBus].getLatLng(), 16);
                    openPanel(askedBus);
                    askedBus = null;
                }

                // Live-update the open side panel with the selected bus's newest data.
                if (selectedVehicleId && busMarkers[selectedVehicleId]) {
                    fillPanel(busMarkers[selectedVehicleId]._bus);
                }
            })
            .catch(function (err) {
                console.error('Failed to load positions:', err);
                setConnectionLost(true); // keep markers as-is and keep polling
            })
            .finally(function () {
                positionsBusy = false;
                if (positionsAgain) { positionsAgain = false; fetchPositions(); }
            });
    }

    // Re-fetch stops, optionally narrowed to one route
    function loadStops(routeId) {
        stopLayer.clearLayers();
        var url = '/FleetMap/Stops' + (routeId ? '?routeId=' + routeId : '');

        fetch(url)
            .then(response => response.json())
            .then(stops => {
                reachTo(stops.map(function (s) { return [s.lat, s.lng]; }));

                stops.forEach(function (stop) {
                    var routeColor = colorForRoute(stop.routeName);
                    var stopIcon = L.divIcon({
                        className: 'fm-stop-marker',
                        html: '<div class="fm-stop-dot" style="background:' + routeColor + '"></div>',
                        iconSize: [24, 24],
                        iconAnchor: [12, 12]
                    });

                    L.marker([stop.lat, stop.lng], { icon: stopIcon })
                        .addTo(stopLayer)
                        .bindTooltip(stop.name, { direction: 'top', offset: [0, -10], className: 'fm-stop-tooltip' });
                });
            })
            .catch(err => console.error('Failed to load stops:', err));
    }

    // Show only the selected route's polylines (null = all)
    function applyRouteFilter(routeId) {
        Object.entries(routePolylines).forEach(([rid, polylines]) => {
            var show = !routeId || parseInt(rid) === routeId;
            polylines.forEach(line => {
                if (show && !map.hasLayer(line)) {
                    line.addTo(map);
                } else if (!show && map.hasLayer(line)) {
                    map.removeLayer(line);
                }
            });
        });
    }

    function buildLegend(routes) {
        if (!legendEl) return;
        legendEl.innerHTML = '';
        routes.filter(function (r) { return r.waypointsJson; }).forEach(function (route) {
            var item = document.createElement('div');
            item.className = 'fm-legend-item';
            var dot = document.createElement('span');
            dot.className = 'fm-legend-dot';
            dot.style.background = colorForRoute(route.routeName);
            item.appendChild(dot);
            item.appendChild(document.createTextNode(route.routeName));
            legendEl.appendChild(item);
        });
    }

    // Shared filter handler — narrows polylines, stops, and live buses together
    function refetch() {
        var routeId = parseInt(routeSelect.value) || null;
        applyRouteFilter(routeId);
        loadStops(routeId);
        fetchPositions();
    }

    // Recenter the map on the live (moving) buses, which matters when testing far from the
    // BGC routes, where a real GPS pin sits outside the route-fitted view. Prefers moving
    // buses (a phone on a trip); falls back to every visible bus if none are moving yet.
    //
    // Each bus counts at the position it reported, not where its marker is drawn. Buses
    // parked at a terminal are fanned out a fixed number of pixels apart, which covers
    // more ground the further out the map is, so fitting to the drawn markers zoomed out
    // a little more on every press. The padding is a share of the map rather than a fixed
    // eighty pixels a side, which on a phone left almost no map to fit into.
    function fitToBuses() {
        var moving = [], all = [];
        Object.keys(busMarkers).forEach(function (id) {
            var m = busMarkers[id];
            if (!m || !busLayer.hasLayer(m)) return;        // skip hidden (search-filtered)
            var at = m._bus ? L.latLng(m._bus.lat, m._bus.lng) : m.getLatLng();
            all.push(at);
            if (m._bus && (m._bus.status === 'On Trip' || m._bus.status === 'Active'))
                moving.push(at);
        });
        var pts = moving.length ? moving : all;
        if (!pts.length) return;
        var size = map.getSize();
        var pad = Math.round(Math.min(80, size.x * 0.12, size.y * 0.12));
        var bounds = L.latLngBounds(pts);
        if (pts.length === 1 || bounds.getNorthEast().equals(bounds.getSouthWest())) map.setView(bounds.getCenter(), 16);
        else map.fitBounds(bounds, { padding: [pad, pad], maxZoom: 16 });
    }
    if (fitBtn) fitBtn.addEventListener('click', fitToBuses);

    // Raw GPS: for checking the snapping on the road. Each bus on a trip also shows the
    // reading its phone sent, a circle as wide as the phone said that reading could be
    // off, and a thread to where the bus is drawn. Off on every visit, since it is for
    // checking the snapping and not how the board is normally read.
    var showRaw = false;

    function drawRaw() {
        rawLayer.clearLayers();
        if (!showRaw) return;

        Object.keys(busMarkers).forEach(function (id) {
            var m = busMarkers[id];
            var bus = m && m._bus;
            if (!bus || bus.status !== 'On Trip' || !busLayer.hasLayer(m)) return;

            var raw = [bus.rawLat, bus.rawLng];
            var color = colorForRoute(bus.routeName);
            if (bus.accuracy != null) {
                L.circle(raw, { radius: bus.accuracy, color: color, weight: 1, opacity: .5, fillOpacity: .08, interactive: false })
                    .addTo(rawLayer);
            }
            L.polyline([raw, [bus.lat, bus.lng]], { color: '#374151', weight: 1.5, dashArray: '3 4', opacity: .7, interactive: false })
                .addTo(rawLayer);
            L.circleMarker(raw, { radius: 4, color: '#fff', weight: 1.5, fillColor: color, fillOpacity: 1, interactive: false })
                .addTo(rawLayer);
        });
    }

    function setRaw(on) {
        showRaw = on;
        if (rawBtn) rawBtn.setAttribute('aria-pressed', on ? 'true' : 'false');
        if (gpsDrawer) gpsDrawer.classList.toggle('fm-drawer--live', on);
        if (on && !map.hasLayer(rawLayer)) rawLayer.addTo(map);
        if (!on && map.hasLayer(rawLayer)) map.removeLayer(rawLayer);
        drawRaw();
    }

    if (rawBtn) {
        rawBtn.addEventListener('click', function () { setRaw(!showRaw); });
        setRaw(showRaw);
    }

    // The clear control only appears once something is actually filtered, so it does not
    // occupy the toolbar when there is nothing to undo.
    function syncClearBtn() {
        if (!clearBtn) return;
        var active = !!(routeSelect.value || statusSelect.value || (searchInput && searchInput.value));
        clearBtn.hidden = !active;
    }

    routeSelect.addEventListener('change', function () { syncClearBtn(); refetch(); });
    statusSelect.addEventListener('change', function () { syncClearBtn(); refetch(); });

    if (clearBtn) {
        clearBtn.addEventListener('click', function () {
            routeSelect.value = '';
            statusSelect.value = '';
            if (searchInput) searchInput.value = '';
            searchTerm = '';
            foldParked();
            syncClearBtn();
            refetch();
        });
    }
    if (searchInput) {
        searchInput.addEventListener('input', function () {
            var had = !!searchTerm;
            searchTerm = searchInput.value.trim().toLowerCase();
            foldParked();
            syncClearBtn();
            applySearch();
            // Only the transitions in and out of searching change which buses the server
            // returns, so the extra request is not made on every keystroke.
            if (had !== !!searchTerm) refetch();
        });
    }

    // Single source of truth for routes: assign colors, draw polylines, fill the
    // dropdown and legend — then start stops + live polling once colors are ready.
    fetch('/FleetMap/Routes')
        .then(r => r.json())
        .then(function (routes) {
            routes.forEach(function (route) {
                var color = colorForRouteId(route.routeId);
                routeColors[route.routeName] = color;

                var option = document.createElement('option');
                option.value = route.routeId;
                option.textContent = route.routeName;
                routeSelect.appendChild(option);

                if (route.waypointsJson) {
                    try {
                        var waypoints = JSON.parse(route.waypointsJson);
                        var latLngs = waypoints.map(w => [w.lat, w.lng]);
                        routeLines[route.routeId] = RouteMotion.measure(latLngs);
                        var polyline = L.polyline(latLngs, {
                            color: color,
                            weight: 5,
                            opacity: 0.8,
                            lineCap: 'round',
                            lineJoin: 'round'
                        }).addTo(map);

                        if (!routePolylines[route.routeId]) routePolylines[route.routeId] = [];
                        routePolylines[route.routeId].push(polyline);
                    } catch (e) {
                        console.error('Error parsing waypoints for route', route.routeName, e);
                    }
                }
            });

            buildLegend(routes);

            loadStops(null);
            fetchPositions();
            setInterval(function () {
                // A hidden tab is nobody looking. Skipping the request costs a
                // few seconds of staleness on return, which the visibility
                // handler below closes immediately.
                if (document.hidden) return;
                fetchPositions();
            }, POLL_INTERVAL_MS);
            // The time to the next stop counts down, and the strip follows the pin, every
            // second between polls.
            setInterval(function () {
                var m = selectedVehicleId && busMarkers[selectedVehicleId];
                if (!m || !m._bus || !m._bus.tripId) return;
                document.getElementById('fmPanelEta').textContent = etaText(m._bus);
                if (m._bus.onRoute && !isStale(m._bus)) placeOnLeg(legProgress(m._bus), m._bus.vehicleId);
            }, 1000);
            document.addEventListener('visibilitychange', function () {
                if (!document.hidden) fetchPositions();
            });
        })
        .catch(err => console.error('Failed to load routes:', err));
})();
