// Field errors, shown under the field they are about and kept until something is typed or
// picked in it.
//
// A message lives in the field's own slot: the data-valmsg-for span a form tag helper
// writes, or a data-err-for="<field id>" span on forms built by script. Slots always take
// their line, so a message appearing does not move the fields below it.
//
// Closing a dialog clears every mark in it. A dialog tagged data-reset-on-close also
// empties its fields, back to data-reset where one is given and to the first option on a
// select, so it opens clean next time rather than holding what was typed before.
(function () {
    function slot(el) {
        return (el.name && el.form && el.form.querySelector('[data-valmsg-for="' + el.name + '"]'))
            || (el.id && document.querySelector('[data-err-for="' + el.id + '"]'));
    }

    function clear(el) {
        if (!el.classList || !el.classList.contains('input-validation-error')) return;
        el.classList.remove('input-validation-error');
        el.removeAttribute('aria-invalid');
        var msg = slot(el);
        if (msg) {
            msg.textContent = '';
            msg.classList.replace('field-validation-error', 'field-validation-valid');
        }
    }

    function mark(el, text) {
        el.classList.add('input-validation-error');
        el.setAttribute('aria-invalid', 'true');
        var msg = slot(el);
        if (msg) {
            msg.textContent = text;
            msg.classList.remove('field-validation-valid');
            msg.classList.add('field-validation-error');
        }
    }

    // For forms built by script: marks the field and puts the message in its slot.
    window.rsFieldError = mark;

    // The rules the server checks, as the form carries them in its data-val attributes,
    // checked before sending so a missed field is marked where it is instead of costing a
    // round trip and a reload.
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

    // A dialog's form that passes is sent in the background. What only the server can
    // know, such as an email already in use, comes back as the page it would have drawn;
    // its messages are lifted out and put under the same fields, and the dialog stays.
    // A save comes back as a redirect, and the page is loaded fresh to show it.
    async function send(form) {
        var button = form.querySelector('[type="submit"]');
        if (button) button.disabled = true;
        try {
            var res = await fetch(form.action, { method: 'POST', body: new FormData(form), redirect: 'manual' });
            if (res.type === 'opaqueredirect') { location.replace(location.href); return; }
            var doc = new DOMParser().parseFromString(await res.text(), 'text/html');
            var back = doc.querySelector('form[action="' + form.getAttribute('action') + '"]');
            var first = null;
            if (back) back.querySelectorAll('.field-validation-error[data-valmsg-for]').forEach(function (m) {
                var el = form.elements[m.getAttribute('data-valmsg-for')];
                if (!el || !el.classList || !m.textContent.trim()) return;
                mark(el, m.textContent.trim());
                first = first || el;
            });
            if (first) { first.focus(); return; }
        } catch (x) { /* fall through to a plain send */ }
        finally { if (button) button.disabled = false; }
        HTMLFormElement.prototype.submit.call(form);
    }

    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!form.querySelector('[data-val="true"]')) return;
        var first = null;
        form.querySelectorAll('[data-val="true"]').forEach(function (el) {
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
        } else if (form.closest('.modal')) {
            e.preventDefault();
            e.stopImmediatePropagation();
            send(form);
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

    function reset(modal, empty) {
        modal.querySelectorAll('.input-validation-error').forEach(clear);
        if (!empty) return;
        modal.querySelectorAll('input, select, textarea').forEach(function (el) {
            if (el.readOnly || el.type === 'hidden' || el.type === 'checkbox' || el.type === 'radio') return;
            if (el.tagName === 'SELECT') el.selectedIndex = 0;
            else el.value = el.dataset.reset || '';
        });
    }

    document.addEventListener('hidden.bs.modal', function (e) {
        reset(e.target, e.target.hasAttribute('data-reset-on-close'));
    });

    // Dialogs drawn by a page's own script call this when they close.
    window.rsClearFieldErrors = function (root) { reset(root, false); };
})();
