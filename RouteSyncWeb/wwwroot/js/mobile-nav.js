// Opens and closes the More sheet on the touch navigation bar.
//
// The bar itself is markup and CSS and needs nothing from here. This holds the open
// state of the sheet and the parts a stylesheet cannot express: what a screen reader
// is told, what the keyboard can reach, and what the back gesture does.
(function () {
    var sheet = document.getElementById('fwSheet');
    if (!sheet) return;

    var button = document.getElementById('fwMoreBtn');
    var backdrop = document.getElementById('fwSheetBackdrop');
    var links = sheet.querySelectorAll('.fw-sheet__who, .fw-sheet__link, .fw-sheet__logout');

    function isOpen() { return sheet.classList.contains('fw-sheet--open'); }

    function setOpen(open) {
        sheet.classList.toggle('fw-sheet--open', open);
        backdrop.classList.toggle('fw-sheet-backdrop--open', open);
        button.setAttribute('aria-expanded', open ? 'true' : 'false');

        // The sheet stays in the page when closed so it can slide, so its links have
        // to be taken out of the tab order by hand.
        links.forEach(function (a) { a.tabIndex = open ? 0 : -1; });
        if (open && links.length) links[0].focus();
    }

    button.addEventListener('click', function () {
        var opening = !isOpen();
        setOpen(opening);
        // A phone's back gesture should close the sheet before it leaves the page,
        // which is what someone who opened it by accident expects.
        if (opening) history.pushState({ fwSheet: true }, '');
    });

    backdrop.addEventListener('click', function () { setOpen(false); });

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && isOpen()) {
            setOpen(false);
            button.focus();
        }
    });

    window.addEventListener('popstate', function () {
        if (isOpen()) setOpen(false);
    });

    // Coming back to a page from the back/forward cache restores the markup as it was
    // left, so an open sheet would return open over a page nobody navigated to.
    window.addEventListener('pageshow', function () { setOpen(false); });
})();
