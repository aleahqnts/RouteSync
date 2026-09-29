// Event handlers named in markup, in place of onclick="…" and the like.
//
// The content security policy refuses inline script, and an on* attribute is inline script
// with no way to carry the nonce. So an element names its handler instead:
//
//     <button data-on="click:schSave">Save Schedule</button>
//
// and a script on the page, which does carry the nonce, supplies the body:
//
//     rsOn({ schSave: function (event) { schSave(); } });
//
// A handler runs as the attribute did: on the element itself rather than delegated from
// above, with `this` set to the element, and a return of false cancels the default action.
// Elements are wired as they enter the page, so rows a page redraws from fetched markup,
// or clones out of a template, answer without anything further. Several events on one
// element are separated by spaces: data-on="input:a change:b".
//
// Loaded in the head, ahead of every page script, so the watch is running before the body
// is parsed and a page's own listeners are added after these, as they were after the
// attributes.
(function () {
    'use strict';

    var handlers = Object.create(null);
    var wired = new WeakSet();

    function listener(name) {
        return function (event) {
            var fn = handlers[name];
            if (!fn) {
                console.error('data-on: nothing registered as "' + name + '"');
                return;
            }
            if (fn.call(this, event) === false) event.preventDefault();
        };
    }

    function wire(el) {
        if (wired.has(el)) return;
        wired.add(el);
        el.getAttribute('data-on').trim().split(/\s+/).forEach(function (pair) {
            var at = pair.indexOf(':');
            if (at > 0) el.addEventListener(pair.slice(0, at), listener(pair.slice(at + 1)));
        });
    }

    function scan(node) {
        if (node.nodeType !== 1) return;
        if (node.hasAttribute('data-on')) wire(node);
        node.querySelectorAll('[data-on]').forEach(wire);
    }

    /** Adds a page's handlers, by the names its markup gives them. */
    window.rsOn = function (map) {
        Object.keys(map).forEach(function (name) {
            if (name in handlers) console.error('data-on: "' + name + '" registered twice');
            handlers[name] = map[name];
        });
    };

    new MutationObserver(function (records) {
        records.forEach(function (r) { r.addedNodes.forEach(scan); });
    }).observe(document.documentElement, { childList: true, subtree: true });

    scan(document.documentElement);
})();
