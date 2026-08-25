// Keeps the keyboard up when a password is revealed.
//
// Every reveal button on every page is a `.eye` beside the field it belongs to, and
// the pages that carry them are rendered by Blazor, so the buttons come and go. One
// listener on the document covers all of them without any page knowing it exists.
(function () {
    document.addEventListener('pointerdown', function (e) {
        var eye = e.target.closest && e.target.closest('.eye');
        if (!eye) return;

        // Pressing the button is what takes focus off the field, and on a phone that
        // closes the keyboard: the driver has to tap the field again to carry on
        // typing. Cancelling the press leaves focus where it is. The click still
        // fires, so the toggle still works.
        e.preventDefault();

        var input = eye.parentNode.querySelector('input');
        if (!input || document.activeElement !== input) return;

        var start = input.selectionStart;
        var end = input.selectionEnd;
        if (start === null) return;

        // Switching a field between password and text drops the caret to the front,
        // so the next character typed lands before the password rather than after it.
        // The switch is done by the render that follows this press, so the caret is
        // put back when the attribute actually changes rather than on a timer.
        //
        // The browser collapses the caret at the end of the press, after the handlers
        // have run, which is later than the observer fires. Yielding once puts the
        // restore behind it.
        var watch = new MutationObserver(function () {
            watch.disconnect();
            setTimeout(function () {
                if (document.activeElement !== input) input.focus();
                try { input.setSelectionRange(start, end); } catch (err) { }
            }, 0);
        });

        watch.observe(input, { attributes: true, attributeFilter: ['type'] });

        // A press that toggles nothing must not leave an observer behind.
        setTimeout(function () { watch.disconnect(); }, 500);
    }, true);
})();
