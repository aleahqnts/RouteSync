// Opens the filter panel that sits behind one button on a phone.
//
// Any page can use it: put data-filter-toggle="<panel id>" on the button and the
// rs-filters class on the panel. The panel and the button only exist at phone width,
// so this does nothing on a screen wide enough to show the filters inline.
(function () {
    document.querySelectorAll('[data-filter-toggle]').forEach(function (button) {
        var panel = document.getElementById(button.getAttribute('data-filter-toggle'));
        if (!panel) return;

        function setOpen(open) {
            panel.classList.toggle('rs-filters--open', open);
            button.setAttribute('aria-expanded', open ? 'true' : 'false');
        }

        button.addEventListener('click', function (e) {
            e.stopPropagation();
            setOpen(!panel.classList.contains('rs-filters--open'));
        });

        // Choosing a filter has done what the panel was opened for. The fleet map
        // filters in place; the pages that submit a form navigate away regardless.
        //
        // Except where a panel holds several fields and a button to apply them, which
        // says so with data-filter-stay: closing on the first choice would take the
        // rest of them away.
        if (!panel.hasAttribute('data-filter-stay')) {
            panel.addEventListener('change', function () { setOpen(false); });
        }

        document.addEventListener('click', function (e) {
            if (!panel.contains(e.target)) setOpen(false);
        });

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') setOpen(false);
        });
    });
})();
