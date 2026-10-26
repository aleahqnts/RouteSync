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

// Swipe down to close. A downward drag that starts on a sheet moves it with the finger.
// Let go past a quarter of its height, or with a flick, and it slides away and closes the
// same way a tap outside does: by tapping the cover. Anything shorter springs back.
// Content scrolled down inside the sheet scrolls first, and a drag that starts in a text
// field is left to the field.
(function () {
    const SHEET = '.nsheet, .lv-modal, .ua-sheet';
    const MS = 200;
    let sheet = null, startY = 0, lastY = 0, lastT = 0, speed = 0, dy = 0, dragging = false;

    function coverOf(s) {
        const prev = s.previousElementSibling;
        return s.closest('.overlay') || (prev && prev.matches('.nscrim') ? prev : null);
    }

    function scrolledDown(target, s) {
        for (let el = target; el && el !== s.parentElement; el = el.parentElement)
            if (el.scrollTop > 0) return true;
        return false;
    }

    function settle(s, close) {
        s.style.transition = close
            ? `transform ${MS}ms cubic-bezier(0.4, 0, 1, 1)`
            : 'transform 0.25s cubic-bezier(0.32, 0.72, 0, 1)';
        s.style.transform = close ? 'translateY(100%)' : '';
        if (!close) return;
        const cover = coverOf(s);
        // The page takes the sheet out on this click. sheets.js above copies it at the
        // bottom edge, where the drag left it, so it does not jump back up to close.
        setTimeout(() => cover && cover.click(), MS);
    }

    document.addEventListener('touchstart', (e) => {
        const s = e.target.closest && e.target.closest(SHEET);
        sheet = null;
        if (!s || e.touches.length > 1 || e.target.closest('input, textarea, select')
            || scrolledDown(e.target, s)) return;
        sheet = s;
        startY = lastY = e.touches[0].clientY;
        lastT = e.timeStamp;
        dy = speed = 0;
        dragging = false;
    }, { passive: true });

    document.addEventListener('touchmove', (e) => {
        if (!sheet) return;
        const y = e.touches[0].clientY;
        dy = y - startY;
        if (!dragging) {
            if (dy < -6) { sheet = null; return; } // upward: a scroll, not a close
            if (dy < 6) return;
            dragging = true;
            // The rise is an animation, and an animation outranks the inline transform.
            sheet.style.animation = 'none';
        }
        e.preventDefault();
        if (e.timeStamp > lastT) speed = (y - lastY) / (e.timeStamp - lastT);
        lastY = y;
        lastT = e.timeStamp;
        sheet.style.transition = 'none';
        sheet.style.transform = `translateY(${Math.max(0, dy)}px)`;
    }, { passive: false });

    function end(e, cancelled) {
        if (!sheet) return;
        const s = sheet;
        sheet = null;
        if (!dragging) return;
        // A flick is a finger still moving down as it lifts, not one that paused first.
        const flick = speed > 0.5 && dy > 24 && e.timeStamp - lastT < 100;
        settle(s, !cancelled && (dy > s.offsetHeight / 4 || flick));
    }

    document.addEventListener('touchend', (e) => end(e, false));
    document.addEventListener('touchcancel', (e) => end(e, true));
})();
