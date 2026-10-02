// Light or dark.
//
// Every colour in the stylesheets is written light-dark(light, dark), so which one is
// drawn follows the color-scheme of the page, set from data-theme on <html>. Bootstrap's
// own parts follow data-bs-theme, kept to the same value.
//
// The page follows the device's setting until someone presses a [data-theme-toggle], and
// from then on keeps what they chose, in this browser.
//
// Loaded in the head, before anything is drawn, so a dark page is dark from its first
// frame rather than flashing white.
(function () {
    'use strict';

    var KEY = 'rs-theme';
    var root = document.documentElement;
    var device = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

    // What was chosen on the switch, or null to follow the device.
    var picked = null;
    try {
        var stored = localStorage.getItem(KEY);
        if (stored === 'light' || stored === 'dark') picked = stored;
    } catch (e) { /* storage refused: follow the device */ }

    function current() {
        return picked || (device && device.matches ? 'dark' : 'light');
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
    function fade() {
        root.classList.add('rs-theme-switching');
        var settle = function () {
            setTimeout(function () { root.classList.remove('rs-theme-switching'); }, 30);
        };
        if (document.startViewTransition && !(still && still.matches)) {
            document.startViewTransition(apply).finished.then(settle, settle);
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

    if (device && device.addEventListener) {
        device.addEventListener('change', function () {
            if (!picked) fade();
        });
    }

    // The switches are in the body, which is not there yet.
    document.addEventListener('DOMContentLoaded', mark);

    document.addEventListener('click', function (e) {
        if (!e.target.closest('[data-theme-toggle]')) return;
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
