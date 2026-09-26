(function () {
    // Read-only Fleet Map preview for the Dashboard card. Pulls the same /FleetMap/Routes
    // and /FleetMap/Positions data as the full map, draws route polylines + live bus pills,
    // and polls every 5s. The map itself is non-interactive — the card links to /FleetMap.
    //
    // Init is deferred to DOMContentLoaded: this script is included mid-body, before the
    // page's inline <style> that gives the map its height, so initializing immediately would
    // size the Leaflet container to 0 and render a blank map.
    function init() {
        var el = document.getElementById('dashFleetMap');
        if (!el || typeof L === 'undefined') return;

        var DEFAULT_CENTER = [14.5508, 121.0509];
        var DEFAULT_ZOOM = 13;
        // Matches the full map: often enough that a boarding shows within seconds of the
        // doorway, affordable because the server reuses its reference lists between reads.
        var POLL_INTERVAL_MS = 2000;

        // Same deterministic route palette as the full map (Route 1 = blue, Route 2 = orange, …).
        var PALETTE = ['#2563EB', '#F97316', '#16A34A', '#DC2626', '#7C3AED', '#0891B2', '#DB2777', '#CA8A04'];

        var map = L.map('dashFleetMap', {
            zoomControl: false,
            dragging: false,
            scrollWheelZoom: false,
            doubleClickZoom: false,
            boxZoom: false,
            keyboard: false,
            touchZoom: false,
            // Kept, small, even on a preview this size. Credit on the map itself is a
            // condition of using OpenStreetMap's tiles, not a courtesy.
            attributionControl: true
        });
        map.attributionControl.setPrefix(false);
        map.setView(DEFAULT_CENTER, DEFAULT_ZOOM);
        RouteMotion.watch(map);

        // Same terms as the full map: the origin is sent as the Referer, which the site-wide
        // header otherwise withholds and without which OpenStreetMap blocks every tile.
        L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            referrerPolicy: 'strict-origin-when-cross-origin',
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
        }).addTo(map);

        var routeColors = {};       // routeName -> color
        var routeLines = {};        // routeId -> measured line the markers travel along
        var busLayer = L.layerGroup().addTo(map);
        var busMarkers = {};        // vehicleId -> marker (moved in place between polls)
        var legendEl = document.getElementById('dashMapLegend');
        var routeBounds = null;

        function colorForRouteId(routeId) {
            var i = ((routeId - 1) % PALETTE.length + PALETTE.length) % PALETTE.length;
            return PALETTE[i];
        }

        function colorForRoute(routeName) {
            return routeColors[routeName] || '#666';
        }

        // Drawn as on the full map: an arrow for the way the bus is going, and a dashed
        // edge when it is off its route and shown where its phone is.
        function busIcon(label, color, off) {
            return L.divIcon({
                className: 'fm-bus-marker' + (off ? ' fm-bus-marker--off' : ''),
                html: '<span style="background:' + color + '">' + RouteMotion.arrowHtml + label + '</span>',
                iconSize: [80, 28],
                iconAnchor: [40, 14]
            });
        }

        // On its route a bus travels the road from one reading to the next; otherwise it
        // is placed where the server put it.
        function moveBus(marker, bus) {
            var line = routeLines[bus.routeId];
            if (bus.status === 'On Trip' && bus.onRoute && bus.along != null && line) {
                RouteMotion.drive(marker, map, line, bus.along, bus.bearing != null, bus.timestamp);
            } else {
                RouteMotion.release(marker);
                marker.setLatLng([bus.lat, bus.lng]);
                RouteMotion.point(marker, bus.status === 'On Trip' ? bus.bearing : null);
            }
        }

        function fetchPositions() {
            fetch('/FleetMap/Positions')
                .then(function (r) { return r.json(); })
                .then(function (buses) {
                    var seen = {};
                    buses.forEach(function (bus) {
                        seen[bus.vehicleId] = true;
                        var color = colorForRoute(bus.routeName);
                        var off = bus.status === 'On Trip' && bus.offRoute;
                        var iconKey = color + (off ? '|off' : '');
                        var marker = busMarkers[bus.vehicleId];
                        if (marker) {
                            // setIcon throws the pill away and builds a new one, so it is
                            // only called when the pill would look different: a change of
                            // route colour, or going off or back onto the route.
                            if (marker._iconKey !== iconKey) {
                                marker.setIcon(busIcon(bus.vehicleId, color, off));
                                marker._iconKey = iconKey;
                                RouteMotion.repoint(marker);
                            }
                        } else {
                            marker = L.marker([bus.lat, bus.lng], { icon: busIcon(bus.vehicleId, color, off), interactive: false })
                                .addTo(busLayer);
                            marker._iconKey = iconKey;
                            busMarkers[bus.vehicleId] = marker;
                        }
                        moveBus(marker, bus);
                    });
                    // Drop buses no longer in the response.
                    Object.keys(busMarkers).forEach(function (id) {
                        if (!seen[id]) {
                            RouteMotion.release(busMarkers[id]);
                            busLayer.removeLayer(busMarkers[id]);
                            delete busMarkers[id];
                        }
                    });
                })
                .catch(function (err) { console.error('Dashboard map positions failed:', err); });
        }

        function buildLegend(routes) {
            if (!legendEl) return;
            legendEl.innerHTML = '';
            routes.filter(function (r) { return r.waypointsJson; }).forEach(function (route) {
                var item = document.createElement('div');
                item.className = 'db-map-legend-item';
                var dot = document.createElement('span');
                dot.className = 'db-dot';
                dot.style.background = colorForRoute(route.routeName);
                item.appendChild(dot);
                item.appendChild(document.createTextNode(' ' + route.routeName));
                legendEl.appendChild(item);
            });
        }

        // Recalculate size (container may have been 0 at init) and refit to the routes.
        function refresh() {
            map.invalidateSize();
            if (routeBounds) map.fitBounds(routeBounds, { padding: [25, 25], maxZoom: 14 });
        }

        fetch('/FleetMap/Routes')
            .then(function (r) { return r.json(); })
            .then(function (routes) {
                var allLatLng = [];
                routes.forEach(function (route) {
                    var color = colorForRouteId(route.routeId);
                    routeColors[route.routeName] = color;
                    if (route.waypointsJson) {
                        try {
                            var latLngs = JSON.parse(route.waypointsJson).map(function (w) { return [w.lat, w.lng]; });
                            routeLines[route.routeId] = RouteMotion.measure(latLngs);
                            L.polyline(latLngs, { color: color, weight: 4, opacity: 0.85, lineCap: 'round', lineJoin: 'round' }).addTo(map);
                            allLatLng = allLatLng.concat(latLngs);
                        } catch (e) { /* skip malformed geometry */ }
                    }
                });

                buildLegend(routes);
                if (allLatLng.length) routeBounds = L.latLngBounds(allLatLng);

                refresh();
                fetchPositions();

                // Only while someone can see the map. A hidden tab is nobody looking, and
                // neither is a map scrolled out of view, which on a phone is most of the
                // time: the card sits below the figures and the chart. Either way the
                // positions are asked for again the moment the map is back in sight, so
                // skipping costs no staleness anyone can see.
                var inView = true;
                function watched() { return inView && !document.hidden; }

                if ('IntersectionObserver' in window) {
                    new IntersectionObserver(function (entries) {
                        var was = inView;
                        inView = entries[entries.length - 1].isIntersecting;
                        if (inView && !was && !document.hidden) fetchPositions();
                    }).observe(el);
                }

                setInterval(function () {
                    if (watched()) fetchPositions();
                }, POLL_INTERVAL_MS);
                document.addEventListener('visibilitychange', function () {
                    if (watched()) fetchPositions();
                });

                // Safety nets: re-assert size after layout/paint settles and on full load.
                setTimeout(refresh, 300);
                window.addEventListener('load', refresh);
            })
            .catch(function (err) { console.error('Dashboard map routes failed:', err); });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
