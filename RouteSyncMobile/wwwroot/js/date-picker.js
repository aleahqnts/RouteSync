// Date fields that open a calendar instead of taking typed digits.
//
// A date input on Android is a row of segments with a caret in it: tapping one puts the
// keyboard up and the driver types the day, the month and the year in whatever order the
// phone happens to print them. The calendar is the control they want, and asking for it
// directly is the only way to be sure it is what opens.
//
// Marked with data-picker-only, so an ordinary date field elsewhere is left alone.
(function () {
    function open(e) {
        var el = e.currentTarget;
        if (typeof el.showPicker !== 'function') return;

        // Not while it is already open: a second call throws, and the throw would reach
        // the tap handler.
        try { el.showPicker(); } catch (err) { }
    }

    function wire(el) {
        if (el.dataset.pickerWired) return;
        el.dataset.pickerWired = '1';
        el.addEventListener('focus', open);
        el.addEventListener('click', open);
        // The segments only take digits from a keyboard, and there is no reason to have
        // one up over a calendar.
        el.addEventListener('keydown', function (ev) {
            if (ev.key !== 'Tab' && ev.key !== 'Escape') ev.preventDefault();
        });
    }

    function sweep() {
        document.querySelectorAll('input[type="date"][data-picker-only]').forEach(wire);
    }

    // The form is drawn when a button is pressed, so the fields do not exist at load.
    new MutationObserver(sweep).observe(document.documentElement, {
        childList: true,
        subtree: true
    });

    sweep();
})();
