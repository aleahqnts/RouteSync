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

    // Colours ease across for a moment rather than jumping, then each element's own
    // transitions apply again.
    var fading = 0;
    function fade() {
        root.classList.add('rs-theme-fade');
        apply();
        clearTimeout(fading);
        fading = setTimeout(function () { root.classList.remove('rs-theme-fade'); }, 320);
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
