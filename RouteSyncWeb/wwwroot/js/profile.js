// The signed-in person's own page.
//
// Every form here posts the same way: sent with fetch, answered with a message per field
// when something is wrong, and followed by a reload when it is saved, since a name or a
// leave request shows in more than one place on the page and the rail. The server
// checks everything again; what is done here only saves a round trip.
(function () {
    var page = document.querySelector('.pf-cols');
    if (!page) return;

    function clearErrors(form) {
        form.querySelectorAll('[data-err]').forEach(function (el) { el.textContent = ''; });
        form.querySelectorAll('.pf-field--bad').forEach(function (el) {
            el.classList.remove('pf-field--bad');
        });
    }

    function showErrors(form, errors) {
        Object.keys(errors || {}).forEach(function (key) {
            var slot = form.querySelector('[data-err="' + key + '"]')
                || form.querySelector('[data-err="form"]');
            if (!slot) return;
            slot.textContent = errors[key];
            var field = slot.closest('.pf-field');
            if (field) field.classList.add('pf-field--bad');
        });
        var first = form.querySelector('.pf-field--bad input, .pf-field--bad select, .pf-field--bad textarea');
        if (first) first.focus();
    }

    // A form that undoes something asks once more before it goes: the first press arms
    // it and says so, and a second within a few seconds sends it.
    function confirmed(form) {
        var question = form.getAttribute('data-confirm');
        if (!question) return true;
        var button = form.querySelector('button[type="submit"]');
        if (form.dataset.armed === '1') return true;

        form.dataset.armed = '1';
        var was = button.textContent;
        button.textContent = question + ' Click again';
        button.classList.add('pf-btn--armed');
        setTimeout(function () {
            form.dataset.armed = '';
            button.textContent = was;
            button.classList.remove('pf-btn--armed');
        }, 4000);
        return false;
    }

    document.querySelectorAll('[data-profile-form]').forEach(function (form) {
        form.addEventListener('submit', async function (e) {
            e.preventDefault();
            if (!confirmed(form)) return;

            clearErrors(form);
            var button = form.querySelector('button[type="submit"]');
            var label = button.textContent;
            button.disabled = true;
            button.textContent = 'Saving…';

            try {
                var res = await fetch(form.getAttribute('action'), { method: 'POST', body: new FormData(form) });
                if (res.ok) {
                    location.reload();
                    return;
                }
                var body = null;
                try { body = await res.json(); } catch (x) { /* not JSON */ }
                showErrors(form, (body && body.errors) || { form: 'This could not be saved. Try again.' });
            } catch (err) {
                showErrors(form, { form: 'This could not be saved. Check your connection and try again.' });
            }

            form.dataset.armed = '';
            button.disabled = false;
            button.textContent = label;
        });
    });

    // Mobile numbers are written as the driver app writes them, 09XX XXX XXXX, while
    // they are typed.
    document.querySelectorAll('[data-phone]').forEach(function (input) {
        input.addEventListener('input', function () {
            var d = input.value.replace(/\D/g, '').slice(0, 11);
            input.value = d.length <= 4 ? d
                : d.length <= 7 ? d.slice(0, 4) + ' ' + d.slice(4)
                : d.slice(0, 4) + ' ' + d.slice(4, 7) + ' ' + d.slice(7);
        });
    });

    // Filing leave opens under the heading and closes again once it has done its job.
    var leaveForm = document.getElementById('pfLeaveForm');
    var leaveOpener = document.querySelector('.pf-card-head [data-leave-toggle]');
    document.querySelectorAll('[data-leave-toggle]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var open = leaveForm.hidden;
            leaveForm.hidden = !open;
            if (leaveOpener) {
                leaveOpener.setAttribute('aria-expanded', open ? 'true' : 'false');
                leaveOpener.hidden = open;
            }
            if (open) leaveForm.querySelector('select').focus();
        });
    });

    // The end date never falls before the start.
    var start = document.querySelector('[data-leave-start]');
    var end = document.querySelector('[data-leave-end]');
    function keepOrder() {
        if (end.value && start.value && end.value < start.value) end.value = start.value;
    }
    if (start && end) {
        start.addEventListener('change', keepOrder);
        end.addEventListener('change', keepOrder);
    }
})();
