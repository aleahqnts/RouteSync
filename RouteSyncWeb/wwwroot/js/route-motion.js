// Moves bus markers the way a navigation app does: continuously along the road, pointing
// the way the bus is going. Shared by the Fleet Map page and the dashboard's map card.
//
// The server places each reading on the bus's route line and says how far along the line
// that is, never further back than the last unless the bus really turned round. This file
// carries the marker on from there at the bus's speed until the next reading. It measures the line exactly as
// the server does, so a distance along it names the same spot in both.
(function () {
    'use strict';

    // Further than this and the marker is put straight there. A bus that went quiet in a dead
    // zone should not be seen racing the whole way it went in the meantime.
    var LONGEST_GLIDE_M = 1500;

    // Below this speed, in metres per second, the bus is standing and is not carried on.
    var MOVING_SPEED = 1;

    // A bus is carried on at its last speed for at most this long after a reading. Past it
    // the bus may be stopped at a light or in a dead zone, and the marker waits for news.
    var PREDICT_FOR_MS = 10000;

    // How old a reading already was when it arrived, judged by the phone's clock, is
    // counted up to this. A phone clock running fast or slow cannot push the marker far.
    var LONGEST_LAG_MS = 3000;

    // Share of the distance to where the bus should be that the marker closes each second,
    // on top of the bus's own speed. Never more than twice the bus's speed, and never less
    // than a slow walk for a bus whose reading moved on while it stood.
    var CATCH_UP = 0.2;
    var FASTEST_FACTOR = 2;
    var SLOWEST_TOP_SPEED = 4;

    // A bus the server says really did turn back is glided back over this long.
    var REVERSE_MS = 1000;

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

    // Where the bus should be now: its last reading carried on at its speed, for as long
    // as that is still believable.
    function predict(anchor, speed, ageMs) {
        if (!(speed >= MOVING_SPEED) || !(ageMs < PREDICT_FOR_MS)) return anchor;
        return anchor + speed * Math.max(ageMs, 0) / 1000;
    }

    // How fast the marker travels, in metres per second, when it is this far behind where
    // the bus should be (negative when ahead of it). At the bus's own speed when level with
    // it, faster to catch up, slower to let the bus catch up, and never backwards.
    function pace(gap, speed) {
        var s = speed >= MOVING_SPEED ? speed : 0;
        var top = Math.max(FASTEST_FACTOR * s, SLOWEST_TOP_SPEED);
        return Math.min(Math.max(s + CATCH_UP * gap, 0), top);
    }

    // The marker's next place on the line, a frame of dt seconds after it was at shown.
    // Returns the place and whether the marker has nothing left to do until news arrives.
    function follow(line, shown, anchor, speed, ageMs, dt) {
        var live = ageMs < PREDICT_FOR_MS && speed >= MOVING_SPEED ? speed : 0;
        var gap = line.ahead(shown, line.wrap(predict(anchor, live, ageMs)));
        return {
            along: line.wrap(shown + pace(gap, live) * dt),
            settled: live === 0 && gap < 0.5
        };
    }

    // Takes a new reading for a marker on its route line. The marker is then driven frame
    // by frame toward where the bus should be, as a navigation app does: carried on at the
    // bus's speed between readings, caught up or held back gently when a reading shows it
    // ahead or behind, and never sent backwards. Only a bus the server has moved back,
    // because it really turned round, is glided back. A reading already taken is ignored,
    // since the map asks more often than the phone reports.
    function drive(marker, map, line, along, moving, readingAt, speed) {
        if (readingAt && readingAt === marker._rmReading && marker._rmLine === line) return;

        var now = Date.now();
        var at = readingTime(readingAt);
        var fresh = marker._rmAlong == null || marker._rmLine !== line;
        var turnedBack = !fresh && marker._rmAnchor != null && line.ahead(marker._rmAnchor, along) < 0;

        var wasMoving = marker._rmSpeed >= MOVING_SPEED;
        marker._rmReading = readingAt;
        // A reading with no speed is one the server ignored as not from the GPS; the bus
        // is still going at the speed it last reported.
        marker._rmSpeed = !moving ? 0 : speed > 0 ? speed : (marker._rmSpeed || 0);
        // The server repeats the last place for a reading that fell behind it. For a moving
        // bus that says nothing new about where it is, so it is still carried on from the
        // time of the place, not of the repeat. A bus that was standing was there until
        // now, so its time starts again from this reading.
        if (!fresh && along === marker._rmAnchor && wasMoving) {
            marker._rmMoving = moving;
            run(marker, map);
            return;
        }
        marker._rmAnchor = along;
        marker._rmSeenMs = now;
        marker._rmLagMs = at == null ? 0 : Math.min(Math.max(now - at, 0), LONGEST_LAG_MS);
        marker._rmMoving = moving;
        marker._rmBack = null;

        if (fresh || Math.abs(line.ahead(marker._rmAlong, along)) > LONGEST_GLIDE_M) {
            place(marker, line, along, moving);
        } else if (turnedBack) {
            marker._rmBack = { from: marker._rmAlong, by: line.ahead(marker._rmAlong, along), start: null };
        }
        run(marker, map);
    }

    function run(marker, map) {
        if (marker._rmFrame) return;
        var last = null;

        function step(now) {
            var line = marker._rmLine;
            if (!line) { marker._rmFrame = null; return; }
            var dt = last == null ? 0 : Math.min((now - last) / 1000, 0.25);
            last = now;

            var a, settled = false, back = marker._rmBack;
            if (back) {
                if (back.start == null) back.start = now;
                var k = Math.min(1, (now - back.start) / REVERSE_MS);
                a = line.wrap(back.from + back.by * k);
                if (k >= 1) marker._rmBack = null;
            } else {
                var age = Date.now() - marker._rmSeenMs + marker._rmLagMs;
                var next = follow(line, marker._rmAlong, marker._rmAnchor, marker._rmSpeed, age, dt);
                a = next.along;
                settled = next.settled;
            }

            marker._rmAlong = a;
            // Leaflet is repositioning every marker while the map zooms; it gets the final
            // word, and the marker carries on from where the zoom leaves it.
            if (!map._rmZooming) marker.setLatLng(line.pointAt(a));
            point(marker, marker._rmMoving ? line.bearingAt(a) : null);
            marker._rmFrame = settled ? null : requestAnimationFrame(step);
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
        marker._rmAnchor = null;
        marker._rmBack = null;
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
        arrowHtml: ARROW_HTML,
        // The motion itself, without a map, for checking.
        predict: predict,
        pace: pace,
        follow: follow
    };
})();
