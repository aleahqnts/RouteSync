// Keyboard behaviour for stacked dialogs, and holding the page still behind them.
//
// Bootstrap listens for Escape on the dialog element, so a dialog that does not trap
// focus never sees the key until something inside is clicked. Several here deliberately
// do not trap it, to keep an overlay above them typeable.
//
// Dialogs built from plain markup have no key handling at all; they name their own close
// function in data-dialog-close. Escape always acts on the frontmost.
(function () {
    // Overlays and sheets that cover the page. Bootstrap's own and the ones that name a
    // close function are found by those marks; the rest are named here because they have
    // no mark of their own.
    var COVERS = [
        '.modal.show',
        '[data-dialog-close]',
        '.db-modal-overlay',
        '.sch-modal-overlay',
        '.dp-msg-overlay',
        '.dp-more-sheet--open',
        '.fw-sheet--open'
    ].join(',');

    function isVisible(el) {
        return el && getComputedStyle(el).display !== 'none';
    }

    function depth(el) {
        return parseInt(getComputedStyle(el).zIndex, 10) || 0;
    }

    /**
     * Whether an overlay actually covers the page, as opposed to sitting over it.
     *
     * A panel the page can be clicked through is not covering it, and locking the
     * scroll behind one is what stops it being read against the thing it describes.
     * The schedule's conflict list is the case: on a desk it is a note beside the grid
     * that can be dragged about while the grid is worked, so the planner could see the
     * cell numbered 1 and had no way to reach the cell numbered 3. On a phone the same
     * panel is an ordinary sheet over a dimmed page, and there it does lock.
     *
     * Read from pointer-events rather than a list kept here, so the stylesheet that
     * decides whether a panel blocks is the one thing that has to say so.
     */
    function covers(el) {
        return getComputedStyle(el).pointerEvents !== 'none';
    }

    /** Every dialog currently on screen, Bootstrap's and this project's own. */
    function openDialogs() {
        var bootstrapDialogs = Array.prototype.slice.call(document.querySelectorAll('.modal.show'));
        var plainDialogs = Array.prototype.slice
            .call(document.querySelectorAll('[data-dialog-close]'))
            .filter(isVisible);
        return bootstrapDialogs.concat(plainDialogs);
    }

    /* ---------------------------------------------------------------------------
       The page behind a dialog.

       Bootstrap locks the body, which does nothing here: the root element carries
       overflow-x on a phone, so the viewport takes its scrolling from there and the
       body's is never consulted. The dialogs built from plain markup lock nothing at
       all. Either way the board scrolls away underneath while a dialog sits still on
       top of it.
       --------------------------------------------------------------------------- */

    /** The box whose scrollbar disappears when the lock goes on. */
    function scroller() {
        return document.documentElement;
    }

    function anythingOpen() {
        var found = document.querySelectorAll(COVERS);
        for (var i = 0; i < found.length; i++) {
            if (isVisible(found[i]) && covers(found[i])) return true;
        }
        return false;
    }

    function setLock(locked) {
        var root = document.documentElement;
        if (root.classList.contains('rs-dialog-open') === locked) return;

        var box = scroller();
        if (locked && box) {
            // Taking the scrollbar away widens the content by its width and shifts
            // every line under the dialog. Its room is kept until the lock is lifted,
            // added to the padding the column already has rather than replacing it.
            var bar = box.offsetWidth - box.clientWidth;
            if (bar > 0) {
                var pad = parseFloat(getComputedStyle(box).paddingRight) || 0;
                box.style.paddingRight = (pad + bar) + 'px';
            }
        } else if (box) {
            box.style.paddingRight = '';
        }

        root.classList.toggle('rs-dialog-open', locked);
    }

    var pending = false;

    function sync() {
        if (pending) return;
        pending = true;
        setTimeout(function () {
            pending = false;
            setLock(anythingOpen());
        }, 0);
    }

    // Dialogs open by having their display or their class written, so those are what
    // is watched. Nothing announces itself, and nothing has to.
    new MutationObserver(sync).observe(document.documentElement, {
        subtree: true,
        attributes: true,
        attributeFilter: ['style', 'class']
    });

    document.addEventListener('shown.bs.modal', sync);
    document.addEventListener('hidden.bs.modal', sync);

    // A panel that lets the page through on a desk blocks it on a phone, and crossing
    // that width changes nothing the observer above is watching.
    window.addEventListener('resize', sync);

    sync();

    /** The one in front, which is the one the keyboard belongs to. */
    function frontDialog() {
        var open = openDialogs();
        if (!open.length) return null;
        return open.reduce(function (a, b) { return depth(b) >= depth(a) ? b : a; });
    }

    function focusFront() {
        var front = frontDialog();
        if (front && front.classList.contains('modal')) front.focus();
    }

    document.addEventListener('shown.bs.modal', function (e) { e.target.focus(); });
    document.addEventListener('hidden.bs.modal', focusFront);

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;

        var front = frontDialog();
        // A Bootstrap dialog in front closes itself, provided it has focus, which the
        // handlers above see to.
        if (!front || !front.hasAttribute('data-dialog-close')) return;

        var close = window[front.getAttribute('data-dialog-close')];
        if (typeof close !== 'function') return;

        e.preventDefault();
        e.stopPropagation();
        close();
        focusFront();
    }, true);

    // Reachable from page scripts that close a dialog by their own route.
    window.focusFrontDialog = focusFront;
})();
