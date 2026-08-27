// The offsets the layers of a list page's head stick at.
//
// A list page holds up to three things at the top of the screen while its rows move:
// the block of controls the page opens with, the heading of the card the list sits in,
// and the column names. Each has to stop below the one above it, so each needs the
// height of the ones above it.
//
// Those heights are not numbers that can be written into the stylesheet. A row of
// filters wraps at narrower widths, a heading takes a second line when a count is
// long, and an alert appears above them both. They are measured here instead and left
// where the stylesheet can read them.
//
// A page opts in by marking its blocks: data-sticky-head on the controls,
// data-sticky-card on the card's heading. Both are optional, and a page with neither
// costs nothing.
(function () {
    var desk = window.matchMedia('(min-width: 600px)');
    var head = null;
    var card = null;
    var queued = false;

    var sizes = window.ResizeObserver ? new ResizeObserver(function () { queue(); }) : null;

    /** The room a block leaves below itself, which its own height does not include. */
    function gapUnder(el) {
        return el ? parseFloat(getComputedStyle(el).marginBottom) || 0 : 0;
    }

    /** Border box height, kept fractional: rounding it opens a seam a row shows through. */
    function heightOf(el) {
        return el ? el.getBoundingClientRect().height : 0;
    }

    function measure() {
        var nextHead = document.querySelector('[data-sticky-head]');
        var nextCard = document.querySelector('[data-sticky-card]');

        // Pages that reload their list replace the card, and the heading inside it,
        // so what is being watched is checked against what is on the page.
        if (nextHead !== head || nextCard !== card) {
            head = nextHead;
            card = nextCard;
            if (sizes) {
                sizes.disconnect();
                if (head) sizes.observe(head);
                if (card) sizes.observe(card);
            }
        }

        var root = document.documentElement;

        // Nothing is held on a phone: the column headings are gone there and each row
        // names its own values, so the layers below the bar are zero.
        var on = desk.matches;
        var lead = parseFloat(getComputedStyle(root).getPropertyValue('--rs-lead')) || 0;

        var headGap = on ? gapUnder(head) : 0;
        var cardGap = on ? gapUnder(card) : 0;

        // Where the layer under the heading starts, which is the far side of whatever
        // is above it. A page whose heading is the first thing held still gets the air
        // above it.
        var underHead = 0;
        if (on && head) underHead = lead + heightOf(head) + headGap;
        else if (on && card) underHead = lead;

        var underCard = underHead;
        if (on && card) underCard = underHead + heightOf(card) + cardGap;

        // A hair of overlap rather than a hair of gap, and one hair per layer, so the
        // overlap does not cancel itself out further down. Sub-pixel heights never
        // divide evenly, and what shows through a seam is a row of the list; what
        // shows through an overlap is the padding of a heading.
        var bite = on ? 1 : 0;

        root.style.setProperty('--rs-head-gap', headGap + 'px');
        root.style.setProperty('--rs-card-gap', cardGap + 'px');
        // The rule a heading draws under itself, which the gap below it starts past.
        root.style.setProperty('--rs-card-edge',
            (on && card ? parseFloat(getComputedStyle(card).borderBottomWidth) || 0 : 0) + 'px');
        root.style.setProperty('--rs-head', Math.max(0, underHead - bite) + 'px');
        root.style.setProperty('--rs-stick', Math.max(0, underCard - bite * 2) + 'px');
    }

    function queue() {
        if (queued) return;
        queued = true;
        requestAnimationFrame(function () {
            queued = false;
            measure();
        });
    }

    // The blocks themselves are replaced wholesale by pages that reload their list
    // without leaving it, which no resize reports.
    new MutationObserver(queue).observe(document.documentElement, {
        childList: true,
        subtree: true
    });

    window.addEventListener('resize', queue);
    window.addEventListener('load', queue);
    if (desk.addEventListener) desk.addEventListener('change', queue);

    measure();
})();
