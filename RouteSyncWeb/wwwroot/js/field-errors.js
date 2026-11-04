// A field the server sent back with an error stays marked until something is typed or
// picked in it, then the mark and its message go.
//
// Closing a dialog clears every mark in it. A dialog tagged data-reset-on-close also
// empties its fields, back to data-reset where one is given and to the first option on a
// select, so it opens clean next time rather than holding what was typed before.
(function () {
    function clear(el) {
        if (!el.classList || !el.classList.contains('input-validation-error')) return;
        el.classList.remove('input-validation-error');
        el.removeAttribute('aria-invalid');
        var scope = el.form || document;
        var msg = scope.querySelector('[data-valmsg-for="' + el.name + '"]');
        if (msg) {
            msg.textContent = '';
            msg.classList.replace('field-validation-error', 'field-validation-valid');
        }
    }

    // The rules the server checks, as the form carries them in its data-val attributes,
    // checked before sending so a missed field is marked where it is instead of costing a
    // round trip and a reload. The server still checks everything, including what only it
    // can know, such as an email already in use.
    function broken(el) {
        var v = el.value.trim(), d = el.dataset;
        if (d.valRequired !== undefined && v === '') return d.valRequired;
        if (v === '') return null;
        if (d.valRange !== undefined) {
            var n = Number(v), lo = Number(d.valRangeMin), hi = Number(d.valRangeMax);
            if (isNaN(n) || n < lo || n > hi) return d.valRange;
        }
        if (d.valLength !== undefined && d.valLengthMax && v.length > Number(d.valLengthMax)) return d.valLength;
        if (d.valEmail !== undefined && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v)) return d.valEmail;
        if (d.valRegex !== undefined && !new RegExp('^(?:' + d.valRegexPattern + ')$').test(v)) return d.valRegex;
        return null;
    }

    function mark(el, text) {
        el.classList.add('input-validation-error');
        el.setAttribute('aria-invalid', 'true');
        var msg = el.form.querySelector('[data-valmsg-for="' + el.name + '"]');
        if (msg) {
            msg.textContent = text;
            msg.classList.remove('field-validation-valid');
            msg.classList.add('field-validation-error');
        }
    }

    document.addEventListener('submit', function (e) {
        var first = null;
        e.target.querySelectorAll('[data-val="true"]').forEach(function (el) {
            if (el.disabled || el.type === 'hidden') return;
            var text = broken(el);
            if (!text) return;
            mark(el, text);
            first = first || el;
        });
        if (first) {
            e.preventDefault();
            e.stopImmediatePropagation();
            first.focus();
        }
    }, true);

    document.addEventListener('input', function (e) { clear(e.target); });
    document.addEventListener('change', function (e) { clear(e.target); });

    // The browser's own checks on email and number boxes would answer first, in a bubble
    // of its own wording, so forms that carry these rules leave the checking to this file.
    document.querySelectorAll('form').forEach(function (f) {
        if (f.querySelector('[data-val="true"]')) f.noValidate = true;
    });

    // Marked fields announce themselves to screen readers too.
    document.querySelectorAll('.input-validation-error').forEach(function (el) {
        el.setAttribute('aria-invalid', 'true');
    });

    document.addEventListener('hidden.bs.modal', function (e) {
        var modal = e.target;
        modal.querySelectorAll('.input-validation-error').forEach(clear);
        if (!modal.hasAttribute('data-reset-on-close')) return;
        modal.querySelectorAll('input, select, textarea').forEach(function (el) {
            if (el.readOnly || el.type === 'hidden' || el.type === 'checkbox' || el.type === 'radio') return;
            if (el.tagName === 'SELECT') el.selectedIndex = 0;
            else el.value = el.dataset.reset || '';
        });
    });
})();
