// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// Pushes an identifier to the right of the name it belongs to, in a column shared
// by every entry in the list.
//
// A native option cannot be laid out: no columns, no alignment, the browser draws
// that popup itself. The only thing that can be controlled is the text, so the name is
// padded to the width of the longest one with a space that does not collapse. The
// selects that use this are set in a monospace face, without which every character
// is a different width and the column drifts.
window.rsIdLabels = function (rows) {
    var widest = 0;
    rows.forEach(function (r) { if (r.name.length > widest) widest = r.name.length; });
    return rows.map(function (r) {
        var pad = new Array(widest - r.name.length + 3).join('\u00A0');
        return r.name + pad + r.id;
    });
};

// Names carried by more than one person in a list. Everywhere else the identifier says
// nothing the name has not already said.
window.rsSharedNames = function (names) {
    var seen = {}, shared = {};
    names.forEach(function (n) {
        var k = (n || '').toLowerCase();
        if (seen[k]) shared[k] = true; else seen[k] = true;
    });
    return function (n) { return !!shared[(n || '').toLowerCase()]; };
};

// Lays a plain name over the closed control of a list built by rsIdLabels above.
//
// The padding that puts the identifiers in a column is only a column while the popup is
// open. Closed, the control draws one line of it, and a name followed by a run of spaces
// and a number is not what anybody wanted to read there. The face carries whatever the
// option's data-face says instead, and the control underneath is untouched: it still
// opens, still takes the keyboard, and still holds the value that gets submitted.
//
// Wrapping is done here rather than in the markup so that a list rebuilt by script keeps
// its face without every caller remembering to.
window.rsPickFace = function (sel) {
    if (!sel) return;

    var wrap = sel.parentElement;
    if (!wrap || !wrap.classList.contains('rs-pick')) {
        wrap = document.createElement('span');
        wrap.className = 'rs-pick';
        sel.parentNode.insertBefore(wrap, sel);
        wrap.appendChild(sel);

        var made = document.createElement('span');
        made.className = 'rs-pick-face';
        made.setAttribute('aria-hidden', 'true');
        wrap.appendChild(made);

        sel.addEventListener('change', function () { window.rsPickFace(sel); });
    }

    var face = wrap.querySelector('.rs-pick-face');
    var opt = sel.selectedOptions[0];
    face.textContent = (sel.value && opt && opt.dataset.face) ? opt.dataset.face : '';
};

// Holds a record open for editing, so a second person opening it sees it read-only.
// Resolves to { holder, release }. holder is null once the hold is taken; otherwise it is
// { by, since } for whoever has it, or { lapsed: true } when the caller's own hold ended
// for being idle. The hold is renewed every 10 seconds while open, counting only clicks
// and typing as activity, and let go by release() or when the page goes away. onLost is
// called with the holder when the hold ends while still open.
window.rsEditLock = function (key, onLost) {
    var active = true, timer = null, done = false;
    function mark() { active = true; }
    function send() {
        var body = new URLSearchParams({ key: key, active: active });
        active = false;
        return fetch('/Locks/Hold', { method: 'POST', body: body }).then(function (r) {
            if (r.ok) return null;
            return r.json().catch(function () { return { by: 'someone else', since: '' }; });
        });
    }
    function release() {
        if (done) return;
        done = true;
        clearInterval(timer);
        document.removeEventListener('pointerdown', mark, true);
        document.removeEventListener('keydown', mark, true);
        window.removeEventListener('pagehide', release);
        navigator.sendBeacon('/Locks/Release', new URLSearchParams({ key: key }));
    }
    return send().then(function (holder) {
        if (holder) return { holder: holder, release: function () {} };
        document.addEventListener('pointerdown', mark, true);
        document.addEventListener('keydown', mark, true);
        window.addEventListener('pagehide', release);
        // A failed renewal is left alone: the hold lapses on the server and the next
        // renewal takes it again, or reports who took it meanwhile.
        timer = setInterval(function () {
            send().then(function (h) { if (h && !done) { release(); if (onLost) onLost(h); } }, function () {});
        }, 10000);
        return { holder: null, release: release };
    });
};

// The one line a read-only editor shows about its holder, written into el: a lock icon,
// the holder's name and the time they opened it. The name gives way first when space is
// short, so the time always shows; the full sentence is the tooltip and the label.
window.rsLockLine = function (el, holder) {
    var icon = document.createElement('i');
    icon.className = 'ti ti-lock';
    icon.setAttribute('aria-hidden', 'true');
    var name = document.createElement('strong');
    var time = document.createElement('span');
    if (holder.lapsed) {
        name.textContent = 'Idle 30 min';
        time.textContent = '· reopen to edit';
        el.title = 'Your lock ended after 30 minutes with no activity. Reopen to edit.';
    } else {
        name.textContent = holder.by;
        time.textContent = holder.since ? '· since ' + holder.since : '';
        el.title = 'Locked by ' + holder.by + (holder.since ? ' since ' + holder.since : '');
    }
    el.setAttribute('aria-label', el.title);
    el.replaceChildren(icon, name, time);
    el.hidden = false;
};
