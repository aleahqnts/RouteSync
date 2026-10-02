// Light or dark.
//
// Every colour in the stylesheets is written light-dark(light, dark), so which one is
// drawn follows the color-scheme of the page, set from data-theme on <html>. Bootstrap's
// own parts follow data-bs-theme, kept to the same value.
//
// The page is light until someone presses a [data-theme-toggle], and from then on keeps
// what they chose, in this browser. The device's own dark setting is not followed: the
// dashboard opens light for everyone who has not asked otherwise.
//
// Loaded in the head, before anything is drawn, so a dark page is dark from its first
// frame rather than flashing white.
(function () {
    'use strict';

    var KEY = 'rs-theme';
    var root = document.documentElement;

    // What was chosen on the switch, or null for the light default.
    var picked = null;
    try {
        var stored = localStorage.getItem(KEY);
        if (stored === 'light' || stored === 'dark') picked = stored;
    } catch (e) { /* storage refused: light */ }

    function current() {
        return picked || 'light';
    }

    function mark() {
        var dark = current() === 'dark';
        document.querySelectorAll('[data-theme-toggle]').forEach(function (b) {
            b.setAttribute('aria-pressed', dark ? 'true' : 'false');
        });
    }

    function apply() {
        var t = current();
        root.setAttribute('data-theme', t);
        root.setAttribute('data-bs-theme', t);
        mark();
        document.dispatchEvent(new CustomEvent('rs-theme', { detail: t }));
    }

    // The page cross-fades from one theme to the other as a single picture, so every part
    // of it changes at the same moment. Each element easing its own colours instead left
    // a busy page half switched for a beat, since no two finished together. Without view
    // transitions in the browser, or with reduced motion asked for, it switches at once.
    //
    // The elements' own colour transitions are held off while it happens, or a button
    // that eases its background would still be easing after the rest had landed.
    var still = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;

    // Switches under way. Quick presses can overlap, so each one counts itself out.
    var switching = 0;

    function fade() {
        switching++;
        root.classList.add('rs-theme-switching');
        var landed = false;
        var settle = function () {
            if (landed) return;
            landed = true;
            setTimeout(function () {
                switching--;
                if (switching > 0) return;
                root.classList.remove('rs-theme-switching');
                releaseRail();
            }, 30);
        };
        if (document.startViewTransition && !(still && still.matches)) {
            var shift = document.startViewTransition(apply);
            shift.finished.then(settle, settle);
            // A transition the browser drops, say for a hidden page, still switches; the
            // drop itself needs no report.
            shift.ready.catch(function () { });
            // A transition waits for the page to draw a frame. One that is not being
            // drawn, behind another window, still switches, and the transition then has
            // nothing left to change.
            setTimeout(function () {
                if (root.getAttribute('data-theme') !== current()) apply();
                settle();
            }, 500);
        } else {
            apply();
            settle();
        }
    }

    apply();

    // The switches are in the body, which is not there yet.
    document.addEventListener('DOMContentLoaded', mark);

    // The rail opens while the pointer is over it. For the moment of the cross-fade the
    // page under the pointer is a picture of itself, so the rail would lose its hover,
    // fold, and open again. It is held open instead.
    //
    // The browser also reports the pointer leaving the rail when the picture goes over
    // it, though it has not moved, so a leave is not believed while a switch is under
    // way. Once the switch lands the rail is let go only if the pointer is really
    // outside it, judged from where the pointer last was.
    var held = null;
    var pointerX = -1, pointerY = -1;

    function notePointer(e) { pointerX = e.clientX; pointerY = e.clientY; }
    document.addEventListener('pointermove', notePointer, { passive: true });
    document.addEventListener('pointerdown', notePointer, { passive: true });

    function letGo() {
        if (!held) return;
        held.classList.remove('fw-sidebar--held');
        held.removeEventListener('mouseleave', onLeave);
        held = null;
    }

    function onLeave() {
        if (switching > 0) return;
        letGo();
    }

    function releaseRail() {
        if (!held) return;
        var box = held.getBoundingClientRect();
        var inside = pointerX >= box.left && pointerX <= box.right
                  && pointerY >= box.top && pointerY <= box.bottom;
        if (!inside) letGo();
    }

    function holdRail(toggle) {
        var rail = toggle.closest('.fw-sidebar');
        if (!rail || held === rail) return;
        letGo();
        held = rail;
        rail.classList.add('fw-sidebar--held');
        rail.addEventListener('mouseleave', onLeave);
    }

    document.addEventListener('click', function (e) {
        var toggle = e.target.closest('[data-theme-toggle]');
        if (!toggle) return;
        holdRail(toggle);
        picked = current() === 'dark' ? 'light' : 'dark';
        try { localStorage.setItem(KEY, picked); } catch (err) { /* kept for this page only */ }
        fade();
    });

    // A chart paints its colours once, so it is drawn again in the new ones. Charts read
    // them through rsTheme.pick as they draw.
    document.addEventListener('rs-theme', function () {
        if (!window.Chart || !window.Chart.instances) return;
        Object.keys(window.Chart.instances).forEach(function (id) {
            window.Chart.instances[id].update('none');
        });
    });

    window.rsTheme = {
        /** The theme being drawn, "light" or "dark". Listen for "rs-theme" on document
            to hear it change. */
        current: current,
        /** The light or the dark of two colours, for what a stylesheet cannot reach: a
            canvas, or a colour handed to a library. */
        pick: function (light, dark) { return current() === 'dark' ? dark : light; }
    };
})();
