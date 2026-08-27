(function () {
    // BGC, Taguig — default center used until route bounds are available
    var DEFAULT_CENTER = [14.5508, 121.0509];
    var DEFAULT_ZOOM = 16;
    var POLL_INTERVAL_MS = 5000;


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

    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
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
    map.on('zoomstart', function () { map.getContainer().classList.add('fm-map--jump'); });
    map.on('zoomend', function () { map.getContainer().classList.remove('fm-map--jump'); });

    var routeColors = {};        // routeName -> color, built from the routes fetch
    var routePolylines = {};     // routeId -> [polylines]
    var stopLayer = L.layerGroup().addTo(map);
    var busLayer = L.layerGroup().addTo(map);
    var terminalLayer = L.layerGroup().addTo(map); // terminal name labels
    var terminalSignature = null;                  // what those labels currently say

    // Parked buses are grouped per terminal and spread into a centred grid (anchored on
    // the terminal point the server sends) so the pills never overlap.
    var TERMINAL_PER_ROW = 4;
    var TERMINAL_DLAT = 0.0006;  // row spacing (south)
    var TERMINAL_DLNG = 0.0011;  // column spacing (pills are wide)

    function terminalSlot(lat, lng, i) {
        var row = Math.floor(i / TERMINAL_PER_ROW);
        var col = i % TERMINAL_PER_ROW;
        return [lat - row * TERMINAL_DLAT, lng + (col - (TERMINAL_PER_ROW - 1) / 2) * TERMINAL_DLNG];
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
    var connBadge = document.getElementById('fmConnBadge');
    var legendEl = document.getElementById('fmLegend');
    var legendToggle = document.getElementById('fmLegendToggle');

    if (legendToggle && legendEl && window.matchMedia('(hover: none) and (pointer: coarse)').matches) {
        legendToggle.setAttribute('aria-expanded', 'false');
        legendEl.hidden = true;
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
        return '#F59E0B'; // amber — Ready to Deploy / Pending / other parked
    }

    // Server timestamps are UTC but serialized without a 'Z', so append one before parsing.
    function relativeTime(ts) {
        var then = new Date(ts + 'Z').getTime();
        if (isNaN(then)) return '';
        var secs = Math.max(0, Math.round((Date.now() - then) / 1000));
        if (secs < 10) return 'just now';
        if (secs < 60) return secs + ' seconds ago';
        var mins = Math.round(secs / 60);
        if (mins === 1) return '1 minute ago';
        if (mins < 60) return mins + ' minutes ago';
        var hrs = Math.round(mins / 60);
        return hrs === 1 ? '1 hour ago' : hrs + ' hours ago';
    }

    function fillPanel(bus) {
        document.getElementById('fmPanelBus').textContent = bus.vehicleId;
        document.getElementById('fmPanelRoute').textContent = String(bus.routeId).padStart(2, '0');
        document.getElementById('fmPanelShift').textContent = bus.shift;
        document.getElementById('fmPanelStatus').textContent = bus.status;
        document.getElementById('fmPanelStatusDot').style.background = statusColor(bus.status);
        document.getElementById('fmPanelDriver').textContent = bus.driverName;
        document.getElementById('fmPanelPax').textContent = bus.passengers;
        // Written as an escape rather than the sign itself: this file carries no
        // byte order mark, so it is decoded as whatever the response says, and a
        // peso sign is the one character here that would not survive being read as
        // anything but UTF-8.
        document.getElementById('fmPanelRevenue').textContent = '\u20B1' + pesoFmt.format(bus.estimatedRevenue);
        document.getElementById('fmPanelUpdated').textContent = 'Last updated: ' + relativeTime(bus.timestamp);
    }

    function openPanel(vehicleId) {
        selectedVehicleId = vehicleId;
        var marker = busMarkers[vehicleId];
        if (marker && marker._bus) fillPanel(marker._bus);
        panel.classList.add('fm-panel--open');
        panel.setAttribute('aria-hidden', 'false');
    }

    function closePanel() {
        selectedVehicleId = null;
        panel.classList.remove('fm-panel--open');
        panel.setAttribute('aria-hidden', 'true');
    }

    panelClose.addEventListener('click', closePanel);

    // A reading older than this is where the bus was, not where it is. Only a trip
    // reports; a bus standing at its terminal is not reporting and is not late for it,
    // and the timestamp it is given is the moment the board was drawn.
    var STALE_AFTER_MS = 2 * 60 * 1000;

    function readingAge(bus) {
        var then = new Date(bus.timestamp + 'Z').getTime();
        return isNaN(then) ? 0 : Math.max(0, Date.now() - then);
    }

    function isStale(bus) {
        return bus.status === 'On Trip' && readingAge(bus) >= STALE_AFTER_MS;
    }

    function busIcon(label, color, stale) {
        return L.divIcon({
            className: 'fm-bus-marker' + (stale ? ' fm-bus-marker--stale' : ''),
            html: '<span style="background:' + color + '">' + label + '</span>',
            iconSize: [80, 28],
            iconAnchor: [40, 14]
        });
    }

    // 3. Gliding is armed a frame after the marker exists. Armed at once, its first
    // position would be animated from the corner of the map: the element is in the
    // page before Leaflet gives it a place.
    function armGlide(marker) {
        requestAnimationFrame(function () {
            var el = marker.getElement();
            if (el) el.classList.add('fm-bus-marker--glide');
        });
    }

    function tooltipHtml(bus) {
        var sc = statusColor(bus.status);
        return '<div class="fm-tooltip">' +
                '<div class="fm-tooltip__header">' +
                    '<span class="fm-tooltip__bus">' + bus.vehicleId + '</span>' +
                    '<span class="fm-tooltip__route">' + bus.routeName + '</span>' +
                '</div>' +
                '<div class="fm-tooltip__plate">' + bus.plateNumber + '</div>' +
                '<div class="fm-tooltip__status" style="color:' + sc + '"><span class="fm-tooltip__dot" style="background:' + sc + '"></span>' + bus.status + '</div>' +
                '<div class="fm-tooltip__passengers"><span>Total Passengers</span><strong>' + bus.passengers + '</strong></div>' +
                (isStale(bus)
                    ? '<div class="fm-tooltip__stale">Last heard from ' + relativeTime(bus.timestamp) + '</div>'
                    : '') +
            '</div>';
    }

    function setConnectionLost(lost) {
        if (connBadge) connBadge.classList.toggle('fm-conn-badge--visible', lost);
    }

    // Add a terminal name label above its parked-bus grid.
    function addTerminalLabel(lat, lng, name, count) {
        var html = '<div class="fm-terminal-pill">🅿 ' + (name || 'Terminal') + ' · ' + count + '</div>';
        L.marker([lat + 0.0006, lng], {
            icon: L.divIcon({ className: 'fm-terminal-label', html: html, iconSize: [200, 26], iconAnchor: [100, 13] }),
            interactive: false,
            zIndexOffset: -500
        }).addTo(terminalLayer);
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

        if (countEl) {
            countEl.innerHTML = shown === 0
                ? ''
                : shown + (shown === 1 ? ' bus' : ' buses') + ', <em>' + moving + ' on trip</em>';
        }
        if (fitBtn) fitBtn.disabled = shown === 0;
    }

    // Poll the live Positions endpoint, honouring the current Route/Status filters.
    function fetchPositions() {
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

        fetch(url)
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
                var keys = Object.keys(parkedGroups).sort();
                var signature = keys.map(function (key) {
                    var g = parkedGroups[key];
                    return key + ':' + g.name + ':' + g.list.length;
                }).join('|');

                keys.forEach(function (key) {
                    var g = parkedGroups[key];
                    g.list.sort(function (a, b) { return a.vehicleId < b.vehicleId ? -1 : 1; });
                    g.list.forEach(function (b, i) { parkedPos[b.vehicleId] = terminalSlot(g.lat, g.lng, i); });
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
                    var iconKey = color + (stale ? '|stale' : '');

                    if (marker) {
                        // Moved in place, and left alone otherwise. Setting the icon
                        // replaces the element it is drawn in, which throws away the
                        // travel between readings and any tooltip open on it, so it is
                        // done only when what the icon shows has changed.
                        marker.setLatLng(pos);
                        if (marker._iconKey !== iconKey) {
                            marker.setIcon(busIcon(bus.vehicleId, color, stale));
                            marker._iconKey = iconKey;
                            armGlide(marker);
                        }
                        marker.setTooltipContent(tooltipHtml(bus));
                    } else {
                        marker = L.marker(pos, { icon: busIcon(bus.vehicleId, color, stale) })
                            .bindTooltip(tooltipHtml(bus), { direction: 'top', offset: [0, -10], className: 'fm-tooltip-wrap' })
                            .addTo(busLayer);
                        marker.on('click', function () { openPanel(this._bus.vehicleId); });
                        marker._iconKey = iconKey;
                        armGlide(marker);
                        busMarkers[bus.vehicleId] = marker;
                    }
                    marker._bus = bus; // keep latest data for tooltip/panel refresh
                });

                // Drop buses that fell out of the response (trip ended or filtered out).
                Object.keys(busMarkers).forEach(function (id) {
                    if (!seen[id]) {
                        busLayer.removeLayer(busMarkers[id]);
                        delete busMarkers[id];
                    }
                });

                applySearch();

                // Live-update the open side panel with the selected bus's newest data.
                if (selectedVehicleId && busMarkers[selectedVehicleId]) {
                    fillPanel(busMarkers[selectedVehicleId]._bus);
                }
            })
            .catch(function (err) {
                console.error('Failed to load positions:', err);
                setConnectionLost(true); // keep markers as-is and keep polling
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

    // Recenter the map on the live (moving) buses — the fix for testing far from the BGC
    // routes, where a real GPS pin sits outside the route-fitted view. Prefers moving buses
    // (a phone on a trip); falls back to every visible bus marker if none are moving yet.
    function fitToBuses() {
        var moving = [], all = [];
        Object.keys(busMarkers).forEach(function (id) {
            var m = busMarkers[id];
            if (!m || !busLayer.hasLayer(m)) return;        // skip hidden (search-filtered)
            all.push(m.getLatLng());
            if (m._bus && (m._bus.status === 'On Trip' || m._bus.status === 'Active'))
                moving.push(m.getLatLng());
        });
        var pts = moving.length ? moving : all;
        if (!pts.length) return;
        if (pts.length === 1) map.setView(pts[0], 16);
        else map.fitBounds(L.latLngBounds(pts), { padding: [80, 80], maxZoom: 16 });
    }
    if (fitBtn) fitBtn.addEventListener('click', fitToBuses);

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
            syncClearBtn();
            refetch();
        });
    }
    if (searchInput) {
        searchInput.addEventListener('input', function () {
            var had = !!searchTerm;
            searchTerm = searchInput.value.trim().toLowerCase();
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
            document.addEventListener('visibilitychange', function () {
                if (!document.hidden) fetchPositions();
            });
        })
        .catch(err => console.error('Failed to load routes:', err));
})();
