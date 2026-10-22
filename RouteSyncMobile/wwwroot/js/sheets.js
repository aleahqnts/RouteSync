// Slide-down close for the bottom sheets. Blazor takes a closed sheet out of the page at
// once, so there is nothing left to animate. This watches for a sheet (or the cover it sits
// in) being removed, puts an inert copy back outside the Blazor root, plays the close on the
// copy and then drops it. The pages need no closing state of their own.
//
// The copy keeps the scoped-CSS attributes, so it looks exactly like the sheet it replaces.
// It sits on top of the page for the length of the animation and takes no touches.
(function () {
    const SHEET = '.nsheet, .lv-modal, .ua-sheet';
    const COVER = '.overlay, .nscrim';
    const MS = 260;

    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

    function animateOut(el) {
        const copy = el.cloneNode(true);
        copy.removeAttribute('id');
        copy.setAttribute('aria-hidden', 'true');
        copy.style.pointerEvents = 'none';
        const parts = [copy, ...copy.querySelectorAll('*')];
        for (const p of parts) {
            if (p.matches(SHEET)) p.classList.add('sheet-closing');
            else if (p.matches(COVER)) p.classList.add('cover-closing');
        }
        document.body.appendChild(copy);
        setTimeout(() => copy.remove(), MS);
    }

    new MutationObserver((mutations) => {
        for (const m of mutations) {
            for (const n of m.removedNodes) {
                if (n.nodeType !== 1 || n.classList.contains('sheet-closing')) continue;
                // A sheet, the cover holding one, or the scrim laid beside one.
                if (n.matches(SHEET) || n.querySelector(SHEET) || n.matches('.nscrim')) animateOut(n);
            }
        }
    }).observe(document.getElementById('app') || document.body, { childList: true, subtree: true });
})();
