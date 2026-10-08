// The needs-attention list on the dashboard and the fleet map: today's trips that cannot
// run as planned, read from the dispatch board, plus buses on a trip that have stopped
// reporting, read from the map's positions. Each line links to where it is dealt with.
(function () {
    var box = document.getElementById('rsAttention');
    if (!box) return;

    var list = box.querySelector('.rs-attn__list');
    var count = box.querySelector('.rs-attn__count');
    // Same rule as the fleet map's stale marker: a trip that has been silent this long.
    var SILENT_AFTER_MS = 2 * 60 * 1000;
    var LABELS = { relief: 'Relief', issue: "Can't run", late: 'Late', fault: 'Fault', signal: 'No signal' };

    function getJson(url) {
        return fetch(url, { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok && !r.redirected ? r.json() : null; })
            .catch(function () { return null; });
    }

    function silentFor(bus) {
        var then = new Date(bus.timestamp + 'Z').getTime();
        return isNaN(then) ? 0 : Date.now() - then;
    }

    function line(kind, text, href) {
        var li = document.createElement('li');
        li.className = 'rs-attn__item rs-attn__item--' + kind;
        var a = document.createElement('a');
        a.href = href;
        a.className = 'rs-attn__link';
        var tag = document.createElement('span');
        tag.className = 'rs-attn__tag';
        tag.textContent = LABELS[kind];
        var msg = document.createElement('span');
        msg.className = 'rs-attn__text';
        msg.textContent = text;
        a.append(tag, msg);
        li.append(a);
        return li;
    }

    function render(trips, buses) {
        // A failed read leaves what is on screen rather than claiming all is clear.
        if (trips === null || buses === null) return;

        var lines = trips.map(function (t) {
            return line(t.kind, t.text, box.dataset.dispatchUrl + '?trip=' + encodeURIComponent(t.tripId));
        });
        buses.forEach(function (b) {
            var ms = silentFor(b);
            if (b.status !== 'On Trip' || ms < SILENT_AFTER_MS) return;
            var mins = Math.round(ms / 60000);
            lines.push(line('signal', b.vehicleId + ' has not reported for ' + mins + ' min.',
                box.dataset.mapUrl + '?bus=' + encodeURIComponent(b.vehicleId)));
        });

        list.replaceChildren.apply(list, lines);
        count.textContent = lines.length ? String(lines.length) : '';
        box.classList.toggle('rs-attn--clear', lines.length === 0);
    }

    function load() {
        if (document.hidden) return;
        Promise.all([getJson(box.dataset.attentionUrl), getJson(box.dataset.positionsUrl)])
            .then(function (r) { render(r[0], r[1]); });
    }

    load();
    setInterval(load, 30000);
    document.addEventListener('visibilitychange', load);
})();
