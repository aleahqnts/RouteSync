// Moves bus markers the way a navigation app does: along the road from one reading to the
// next, pointing the way the bus is going. Shared by the Fleet Map page and the dashboard's
// map card.
//
// The server places each reading on the bus's route line and says how far along the line
// that is. This file only animates between two such places. It measures the line exactly as
// the server does, so a distance along it names the same spot in both.
(function () {
    'use strict';

    // Behind by less than this is the fix wandering while the bus stands at a stop, not the
    // bus reversing, so the marker stays where it is.
    var JITTER_BACK_M = 30;

    // Further than this and the marker is put straight there. A bus that went quiet in a dead
    // zone should not be seen racing the whole way it went in the meantime.
    var LONGEST_GLIDE_M = 1500;

    // A glide takes as long as the phone took between the two readings, so the marker
    // arrives about when the next one does. Bounded, because a gap can be anything.
    var SHORTEST_GLIDE_MS = 1000;
    var LONGEST_GLIDE_MS = 8000;
    var USUAL_GAP_MS = 5000;

    // Ends this close together make the line a loop, as on the server.
    var LOOP_CLOSURE_M = 30;

    var ARROW_HTML =
        '<i class="fm-bus-arrow" hidden><svg viewBox="0 0 12 12" aria-hidden="true">' +
        '<path d="M6 .8 10.8 11 6 8.6 1.2 11Z" fill="currentColor"/></svg></i>';

    // A route line, measured on a flat projection about its first point with the same
    // WGS 84 series the server uses.
    function measure(latLngs) {
        if (!latLngs || latLngs.length < 2) return null;

        var lat0 = latLngs[0][0], lng0 = latLngs[0][1];
        var phi = lat0 * Math.PI / 180;
        var mLat = 111132.92 - 559.82 * Math.cos(2 * phi) + 1.175 * Math.cos(4 * phi);
        var mLng = 111412.84 * Math.cos(phi) - 93.5 * Math.cos(3 * phi);

        var n = latLngs.length, x = new Array(n), y = new Array(n);
        var along = new Array(n), bearing = new Array(n - 1);
        for (var i = 0; i < n; i++) {
            x[i] = (latLngs[i][1] - lng0) * mLng;
            y[i] = (latLngs[i][0] - lat0) * mLat;
        }
        along[0] = 0;
        for (i = 1; i < n; i++) {
            var dx = x[i] - x[i - 1], dy = y[i] - y[i - 1];
            along[i] = along[i - 1] + Math.sqrt(dx * dx + dy * dy);
            bearing[i - 1] = (Math.atan2(dx, dy) * 180 / Math.PI + 360) % 360;
        }

        var length = along[n - 1];
        var isLoop = Math.hypot(x[n - 1] - x[0], y[n - 1] - y[0]) <= LOOP_CLOSURE_M;

        function wrap(a) {
            return isLoop ? ((a % length) + length) % length : Math.min(Math.max(a, 0), length);
        }

        function segmentAt(a) {
            var lo = 0, hi = n - 2;
            while (lo < hi) {
                var mid = (lo + hi + 1) >> 1;
                if (along[mid] <= a) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        return {
            length: length,
            isLoop: isLoop,
            wrap: wrap,
            pointAt: function (a) {
                a = wrap(a);
                var i = segmentAt(a);
                var span = along[i + 1] - along[i];
                var t = span > 0 ? Math.min(Math.max((a - along[i]) / span, 0), 1) : 0;
                return [lat0 + (y[i] + t * (y[i + 1] - y[i])) / mLat,
                        lng0 + (x[i] + t * (x[i + 1] - x[i])) / mLng];
            },
            bearingAt: function (a) { return bearing[segmentAt(wrap(a))]; },
            // How far ahead a place is from another, going the short way round a loop.
            // Negative when it is behind.
            ahead: function (from, to) {
                var d = to - from;
                if (!isLoop) return d;
                d = ((d % length) + length) % length;
                return d <= length / 2 ? d : d - length;
            }
        };
    }

    // Points the marker's arrow, or hides it for a bus that is standing still.
    function point(marker, bearing) {
        marker._rmBearing = bearing;
        var el = marker.getElement && marker.getElement();
        var arrow = el && el.querySelector('.fm-bus-arrow');
        if (!arrow) return;
        if (bearing == null) {
            arrow.hidden = true;
        } else {
            arrow.hidden = false;
            arrow.style.transform = 'rotate(' + Math.round(bearing) + 'deg)';
        }
    }

    function stop(marker) {
        if (marker._rmFrame) cancelAnimationFrame(marker._rmFrame);
        marker._rmFrame = null;
    }

    function readingTime(ts) {
        var t = Date.parse(ts + 'Z');
        return isNaN(t) ? null : t;
    }

    // Moves a marker to a new reading on its route line: along the road when it is a
    // short way ahead, straight there when it is far or behind, not at all when it is only
    // the fix wandering at a stop. A reading already acted on is ignored, since the map
    // asks more often than the phone reports.
    function drive(marker, map, line, along, moving, readingAt) {
        if (readingAt && readingAt === marker._rmReading && marker._rmLine === line) return;

        var at = readingTime(readingAt);
        var gap = (at != null && marker._rmReadingMs != null) ? at - marker._rmReadingMs : USUAL_GAP_MS;
        marker._rmReading = readingAt;
        marker._rmReadingMs = at;

        var from = marker._rmAlong;
        if (from == null || marker._rmLine !== line) {
            place(marker, line, along, moving);
            return;
        }

        var d = line.ahead(from, along);
        if (d < 0 && -d < JITTER_BACK_M) {
            point(marker, moving ? line.bearingAt(from) : null);
            return;
        }
        if (d <= 0 || d > LONGEST_GLIDE_M) {
            place(marker, line, along, moving);
            return;
        }

        var ms = Math.min(Math.max(gap, SHORTEST_GLIDE_MS), LONGEST_GLIDE_MS);
        var start = null;
        stop(marker);

        function step(now) {
            if (start == null) start = now;
            var k = Math.min(1, (now - start) / ms);
            var a = line.wrap(from + d * k);
            marker._rmAlong = a;
            // Leaflet is repositioning every marker while the map zooms; it gets the final
            // word, and the glide carries on from where the zoom leaves it.
            if (!map._rmZooming) marker.setLatLng(line.pointAt(a));
            point(marker, moving ? line.bearingAt(a) : null);
            marker._rmFrame = k < 1 ? requestAnimationFrame(step) : null;
        }
        marker._rmFrame = requestAnimationFrame(step);
    }

    function place(marker, line, along, moving) {
        stop(marker);
        marker._rmLine = line;
        marker._rmAlong = along;
        marker.setLatLng(line.pointAt(along));
        point(marker, moving ? line.bearingAt(along) : null);
    }

    // Hands a marker back to plain positioning, for a bus off its route or parked.
    function release(marker) {
        stop(marker);
        marker._rmLine = null;
        marker._rmAlong = null;
        marker._rmReading = null;
        marker._rmReadingMs = null;
    }

    // Lets glides stand aside while the map zooms.
    function watch(map) {
        map.on('zoomstart', function () { map._rmZooming = true; });
        map.on('zoomend', function () { map._rmZooming = false; });
    }

    window.RouteMotion = {
        measure: measure,
        drive: drive,
        release: release,
        point: point,
        // Puts the arrow back after a marker's icon is replaced, which builds a new one.
        repoint: function (marker) { point(marker, marker._rmBearing); },
        watch: watch,
        arrowHtml: ARROW_HTML
    };
})();
