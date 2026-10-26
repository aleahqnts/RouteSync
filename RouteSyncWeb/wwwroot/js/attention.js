// The needs-attention list on the dashboard and the fleet map: today's trips that cannot
// run as planned, read from the dispatch board, plus buses on a trip that have stopped
// reporting, read from the map's positions. Each line links to where it is dealt with.
(function () {
    var box = document.getElementById('rsAttention');
    if (!box) return;

    var list = box.querySelector('.rs-attn__list');
    var count = box.querySelector('.rs-attn__count');
    var more = box.querySelector('.rs-attn__more');
    // Same rule as the fleet map's stale marker: a trip that has been silent this long.
    var SILENT_AFTER_MS = 2 * 60 * 1000;
    // Lines shown before the rest fold behind "Show more".
    var SHOWN = 5;
    // This many of one kind read as one line rather than a column of near-identical ones.
    var GROUP_AT = 3;
    var LABELS = { relief: 'Relief', issue: 'Cannot run', late: 'Late', camera: 'Camera', fault: 'Fault', signal: 'No signal' };
    var GROUPED = {
        issue: function (n) { return n + ' trips cannot run as assigned'; },
        late: function (n) { return n + ' trips have not started'; },
        camera: function (n) { return n + ' buses are not counting passengers'; },
        fault: function (n) { return n + ' buses have an open fault'; },
        signal: function (n) { return n + ' buses on a trip have not reported'; }
    };
    var expanded = false;

    function getJson(url) {
        return fetch(url, { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok && !r.redirected ? r.json() : null; })
            .catch(function () { return null; });
    }

    function silentFor(bus) {
        var then = new Date(bus.timestamp + 'Z').getTime();
        return isNaN(then) ? 0 : Date.now() - then;
    }

    // Hours and minutes, as the dispatch board's late badge writes them.
    function duration(mins) {
        if (mins < 60) return mins + 'm';
        var h = Math.floor(mins / 60), m = mins % 60;
        return m ? h + 'h ' + m + 'm' : h + 'h';
    }

    // Buses as quiet pills. Past this many the rest are counted rather than shown.
    var PILLS = 8;
    function pills(ids) {
        var row = document.createElement('span');
        row.className = 'rs-attn__buses';
        ids.slice(0, PILLS).forEach(function (id) {
            var p = document.createElement('span');
            p.className = 'rs-attn__bus';
            p.textContent = id;
            row.append(p);
        });
        if (ids.length > PILLS) {
            var rest = document.createElement('span');
            rest.className = 'rs-attn__bus rs-attn__bus--more';
            rest.textContent = '+' + (ids.length - PILLS);
            row.append(rest);
        }
        return row;
    }

    // A bus named in a line's text, set as the same pill the grouped lines use.
    function pill(id) {
        var p = document.createElement('span');
        p.className = 'rs-attn__bus rs-attn__bus--inline';
        p.textContent = id;
        return p;
    }

    function line(kind, text, sub, href, buses, bus) {
        var li = document.createElement('li');
        li.className = 'rs-attn__item rs-attn__item--' + kind;
        var a = document.createElement('a');
        a.href = href;
        a.className = 'rs-attn__link';
        var tag = document.createElement('span');
        tag.className = 'rs-attn__tag';
        tag.textContent = LABELS[kind];
        var body = document.createElement('span');
        body.className = 'rs-attn__body';
        var msg = document.createElement('span');
        msg.className = 'rs-attn__text';
        if (bus) {
            text.split(bus).forEach(function (part, i) {
                if (i) msg.append(pill(bus));
                msg.append(part);
            });
        } else {
            msg.textContent = text;
        }
        body.append(msg);
        if (buses) body.append(pills(buses));
        if (sub) {
            var s = document.createElement('span');
            s.className = 'rs-attn__sub';
            s.textContent = sub;
            body.append(s);
        }
        a.append(tag, body);
        li.append(a);
        return li;
    }

    function tripsHref(ids) {
        return box.dataset.dispatchUrl + '?trip=' + ids.map(encodeURIComponent).join(',');
    }

    // One entry per kind once there are enough of it, kept in the order the server ranked
    // them, which is most urgent first. A driver needing relief is never folded away.
    function lines(entries) {
        var byKind = {};
        entries.forEach(function (e) { (byKind[e.kind] = byKind[e.kind] || []).push(e); });
        var done = {};
        var out = [];
        entries.forEach(function (e) {
            var same = byKind[e.kind];
            if (same.length < GROUP_AT || !GROUPED[e.kind]) {
                out.push(line(e.kind, e.text, null, e.href, null, e.vehicleId));
                return;
            }
            if (done[e.kind]) return;
            done[e.kind] = true;
            var ids = same.map(function (x) { return x.vehicleId; });
            var sub = null;
            if (e.kind === 'late') {
                var worst = Math.max.apply(null, same.map(function (x) { return x.minutes || 0; }));
                sub = 'Longest ' + duration(worst) + ' late';
            }
            var href = e.kind === 'signal'
                ? box.dataset.mapUrl
                : tripsHref(same.map(function (x) { return x.tripId; }));
            out.push(line(e.kind, GROUPED[e.kind](same.length), sub, href, ids));
        });
        return out;
    }

    function render(trips, buses) {
        // A failed read leaves what is on screen rather than claiming all is clear.
        if (trips === null || buses === null) return;

        var entries = trips.map(function (t) {
            return {
                kind: t.kind, text: t.text, vehicleId: t.vehicleId, tripId: t.tripId,
                minutes: t.lateMinutes, href: tripsHref([t.tripId])
            };
        });
        buses.forEach(function (b) {
            var ms = silentFor(b);
            if (b.status !== 'On Trip' || ms < SILENT_AFTER_MS) return;
            entries.push({
                kind: 'signal', vehicleId: b.vehicleId,
                text: b.vehicleId + ' has not reported for ' + duration(Math.round(ms / 60000)) + '.',
                href: box.dataset.mapUrl + '?bus=' + encodeURIComponent(b.vehicleId)
            });
        });

        var all = lines(entries);
        all.forEach(function (li, i) { li.hidden = !expanded && i >= SHOWN; });
        list.replaceChildren.apply(list, all);

        var hidden = all.length - SHOWN;
        more.hidden = hidden <= 0;
        more.textContent = expanded ? 'Show fewer' : 'Show ' + hidden + ' more';
        more.setAttribute('aria-expanded', String(expanded));

        count.textContent = entries.length ? String(entries.length) : '';
        box.classList.toggle('rs-attn--clear', entries.length === 0);
        setMood(entries);
    }

    // ---- Bao ----
    var host = box.parentElement;
    var baos = host.querySelectorAll('.bao');
    var stage = host.querySelector('.bao-stage');
    var calm = window.matchMedia('(prefers-reduced-motion: reduce)');
    // A driver who cannot run or needs relief distresses him; anything else listed
    // worries him.
    var DISTRESS = { issue: true, relief: true };

    function setMood(entries) {
        var mood = entries.some(function (e) { return DISTRESS[e.kind]; }) ? 'distressed'
            : entries.length ? 'worried' : 'relaxed';
        box.dataset.mood = mood;
        baos.forEach(function (svg) {
            svg.classList.toggle('face-worried', mood === 'worried');
            svg.classList.toggle('face-distressed', mood === 'distressed');
        });
    }

    function replay(el, cls, ms) {
        el.classList.remove(cls);
        void el.getBoundingClientRect();
        el.classList.add(cls);
        setTimeout(function () { el.classList.remove(cls); }, ms);
    }

    // Poking him shoves him away from the side the click landed on.
    function poke(svg, x) {
        var r = svg.getBoundingClientRect();
        svg.style.setProperty('--bao-shove', x < r.left + r.width / 2 ? 1 : -1);
        replay(svg, 'bao--poke', 650);
    }
    baos.forEach(function (svg) {
        svg.addEventListener('click', function (e) { poke(svg, e.clientX); });
    });

    // Over the map his head is the tab's icon, so opening the tab from its text pokes him
    // too. A click on him has already poked him on its way up.
    if (!stage) {
        box.querySelector('.rs-attn__head').addEventListener('click', function (e) {
            if (box.open) return;
            baos.forEach(function (svg) { if (!svg.contains(e.target)) poke(svg, e.clientX); });
        });
    }

    // His eyes follow the pointer and his head turns toward it. Pupil offsets are in
    // drawing units; the head turn is a -1 to 1 vector that site.css scales per part.
    var pupils = host.querySelectorAll('.bao-pupil');
    var LOOK = 22;
    var pointer = null, queued = false;
    function toward(r, fy) {
        var dx = pointer.x - (r.left + r.width / 2), dy = pointer.y - (r.top + r.height * fy);
        var d = Math.hypot(dx, dy) || 1, k = Math.min(1, d / 160) / d;
        return [dx * k, dy * k];
    }
    function look() {
        queued = false;
        pupils.forEach(function (p) {
            var v = toward(p.parentNode.getBoundingClientRect(), .5);
            p.setAttribute('transform', 'translate(' + (v[0] * LOOK).toFixed(1) + ' ' + (v[1] * LOOK).toFixed(1) + ')');
        });
        if (calm.matches) return;
        baos.forEach(function (svg) {
            var v = toward(svg.querySelector('.bao-head-base').getBoundingClientRect(), .45);
            svg.style.setProperty('--bao-lx', v[0].toFixed(3));
            svg.style.setProperty('--bao-ly', v[1].toFixed(3));
        });
    }
    if (pupils.length) {
        document.addEventListener('pointermove', function (e) {
            if (e.pointerType !== 'mouse') return;
            pointer = { x: e.clientX, y: e.clientY };
            if (!queued) { queued = true; requestAnimationFrame(look); }
        });
        // When the pointer leaves the window he looks ahead again.
        document.documentElement.addEventListener('mouseleave', function () {
            pupils.forEach(function (p) { p.removeAttribute('transform'); });
            baos.forEach(function (svg) {
                svg.style.removeProperty('--bao-lx');
                svg.style.removeProperty('--bao-ly');
            });
        });
    }

    // On the dashboard he works the caret: the panel folds or opens when his hand
    // lands on it, a moment after the click. Clicks during the reach are ignored.
    var REACH_MS = 450, TAP_MS = 900, tapping = false;
    if (stage) {
        box.querySelector('.rs-attn__head').addEventListener('click', function (e) {
            if (calm.matches) return;
            e.preventDefault();
            if (tapping) return;
            tapping = true;
            replay(stage, 'bao-stage--tap', TAP_MS);
            setTimeout(function () { box.open = !box.open; }, REACH_MS);
            setTimeout(function () { tapping = false; }, TAP_MS);
        });
    }

    more.addEventListener('click', function () {
        expanded = !expanded;
        var items = list.children;
        for (var i = SHOWN; i < items.length; i++) items[i].hidden = !expanded;
        more.textContent = expanded ? 'Show fewer' : 'Show ' + (items.length - SHOWN) + ' more';
        more.setAttribute('aria-expanded', String(expanded));
    });

    function load() {
        if (document.hidden) return;
        Promise.all([getJson(box.dataset.attentionUrl), getJson(box.dataset.positionsUrl)])
            .then(function (r) { render(r[0], r[1]); });
    }

    load();
    setInterval(load, 30000);
    document.addEventListener('visibilitychange', load);
})();
