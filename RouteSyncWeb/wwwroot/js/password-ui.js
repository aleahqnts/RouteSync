// Password field behaviour shared by the change and reset pages.
//
// A reveal toggle per field, a live checklist of the policy rules, and a Caps Lock
// warning.
// None is a security control. The server decides what it will accept; this only
// saves the user a round trip to find out.
(function () {
    // One toggle per field. The browser's native reveal control is hidden in CSS
    // because it only renders while the field has focus, so it disappears as soon
    // as you move to the next box.
    document.querySelectorAll('.eye').forEach(function (btn) {
        var input = document.getElementById(btn.getAttribute('data-for'));
        if (!input) return;

        // Pressing the button is what takes focus off the field, and on a phone that
        // closes the keyboard and costs a second tap to get it back. The press is
        // cancelled before focus moves; the click still fires.
        var keepFocus = function (e) { e.preventDefault(); };
        if (window.PointerEvent) btn.addEventListener('pointerdown', keepFocus);
        else btn.addEventListener('mousedown', keepFocus);

        btn.addEventListener('click', function () {
            var reveal = input.type === 'password';
            var typing = document.activeElement === input;
            var start = input.selectionStart;
            var end = input.selectionEnd;

            input.type = reveal ? 'text' : 'password';
            btn.classList.toggle('showing', reveal);
            btn.setAttribute('aria-label', reveal ? 'Hide password' : 'Show password');

            // Changing the type collapses the caret to the front of the field, so
            // the next character typed lands before the password rather than after
            // it. The browser does that at the end of the press, after this handler,
            // so yielding once puts the restore behind it.
            //
            // Only for someone who was already typing: a toggle before the field is
            // touched must not summon a keyboard.
            if (typing && start !== null) {
                setTimeout(function () {
                    if (document.activeElement !== input) input.focus();
                    try { input.setSelectionRange(start, end); } catch (err) { }
                }, 0);
            }
        });
    });

    var field = document.getElementById('NewPassword');
    var rules = document.getElementById('pwRules');
    if (!field || !rules) return;

    // Minimum comes from the server-side policy through a data attribute, so the
    // number lives in one place.
    var min = parseInt(rules.getAttribute('data-min'), 10) || 8;

    var tests = {
        len: function (v) { return v.length >= min; },
        upper: function (v) { return /[A-Z]/.test(v); },
        lower: function (v) { return /[a-z]/.test(v); },
        digit: function (v) { return /[0-9]/.test(v); }
    };

    function paint() {
        var value = field.value;
        rules.querySelectorAll('li').forEach(function (li) {
            var test = tests[li.getAttribute('data-rule')];
            li.classList.toggle('met', !!test && test(value));
        });
    }

    field.addEventListener('input', paint);
    paint();
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
