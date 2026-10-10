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
    var LABELS = { relief: 'Relief', issue: 'Cannot run', late: 'Late', fault: 'Fault', signal: 'No signal' };
    var GROUPED = {
        issue: function (n) { return n + ' trips cannot run as assigned'; },
        late: function (n) { return n + ' trips have not started'; },
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
