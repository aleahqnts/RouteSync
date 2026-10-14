// Keeps the keyboard up when a password is revealed, and warns when Caps Lock is on.
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

// Caps Lock warning for every password field on the page.
//
// Only a key press or a pointer press can report the lock state, so the warning
// appears with the first key or tap in a password field and then follows every
// change until the field loses focus. It floats under the field in a node of its own
// on the body, so no page has to make room for it.
(function () {
    var warn = document.createElement('div');
    warn.id = 'capsWarn';
    warn.className = 'caps-warn';
    warn.setAttribute('role', 'status');
    document.body.appendChild(warn);
    var field = null;

    // A revealed password is a text field, so the reveal button beside it is what
    // still marks it as a password.
    function isPassword(el) {
        return !!el && el.tagName === 'INPUT' &&
            (el.type === 'password' || !!(el.parentNode && el.parentNode.querySelector('.eye')));
    }

    function place() {
        if (!field) return;
        if (!field.isConnected) { hide(); return; }
        var box = field.parentNode.getBoundingClientRect();
        warn.style.top = (box.bottom + 6) + 'px';
        warn.style.left = box.left + 'px';
    }

    function hide() {
        if (field) {
            var ids = (field.getAttribute('aria-describedby') || '').split(' ')
                .filter(function (id) { return id && id !== warn.id; });
            if (ids.length) field.setAttribute('aria-describedby', ids.join(' '));
            else field.removeAttribute('aria-describedby');
        }
        field = null;
        warn.textContent = '';
    }

    function show(input) {
        if (field !== input) {
            hide();
            field = input;
            var ids = (input.getAttribute('aria-describedby') || '').split(' ').filter(Boolean);
            ids.push(warn.id);
            input.setAttribute('aria-describedby', ids.join(' '));
        }
        warn.textContent = 'Caps Lock is on';
        place();
    }

    function check(e) {
        if (!isPassword(e.target) || !e.getModifierState) return;
        if (e.getModifierState('CapsLock')) show(e.target);
        else if (field === e.target) hide();
    }

    document.addEventListener('keydown', check, true);
    document.addEventListener('keyup', check, true);
    document.addEventListener('pointerdown', check, true);
    document.addEventListener('focusout', function (e) { if (e.target === field) hide(); }, true);
    window.addEventListener('scroll', place, true);
    window.addEventListener('resize', place);
})();
