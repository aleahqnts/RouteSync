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
        var POLL_INTERVAL_MS = 5000;

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

        // Same terms as the full map: the origin is sent as the Referer, which the site-wide
        // header otherwise withholds and without which OpenStreetMap blocks every tile.
        L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            referrerPolicy: 'strict-origin-when-cross-origin',
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
        }).addTo(map);

        var routeColors = {};       // routeName -> color
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

        function busIcon(label, color) {
            return L.divIcon({
                className: 'fm-bus-marker',
                html: '<span style="background:' + color + '">' + label + '</span>',
                iconSize: [80, 28],
                iconAnchor: [40, 14]
            });
        }

        function fetchPositions() {
            fetch('/FleetMap/Positions')
                .then(function (r) { return r.json(); })
                .then(function (buses) {
                    var seen = {};
                    buses.forEach(function (bus) {
                        seen[bus.vehicleId] = true;
                        var color = colorForRoute(bus.routeName);
                        var pos = [bus.lat, bus.lng];
                        var marker = busMarkers[bus.vehicleId];
                        if (marker) {
                            marker.setLatLng(pos);
                            marker.setIcon(busIcon(bus.vehicleId, color));
                        } else {
                            marker = L.marker(pos, { icon: busIcon(bus.vehicleId, color), interactive: false })
                                .addTo(busLayer);
                            busMarkers[bus.vehicleId] = marker;
                        }
                    });
                    // Drop buses no longer in the response.
                    Object.keys(busMarkers).forEach(function (id) {
                        if (!seen[id]) {
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
                            L.polyline(latLngs, { color: color, weight: 4, opacity: 0.85, lineCap: 'round', lineJoin: 'round' }).addTo(map);
                            allLatLng = allLatLng.concat(latLngs);
                        } catch (e) { /* skip malformed geometry */ }
                    }
                });

                buildLegend(routes);
                if (allLatLng.length) routeBounds = L.latLngBounds(allLatLng);

                refresh();
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
