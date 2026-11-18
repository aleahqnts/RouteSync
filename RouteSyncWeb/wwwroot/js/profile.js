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

    // A field's message goes once something is typed in it.
    page.addEventListener('input', function (e) {
        var field = e.target.closest && e.target.closest('.pf-field--bad');
        if (!field) return;
        field.classList.remove('pf-field--bad');
        var slot = field.querySelector('[data-err]');
        if (slot) slot.textContent = '';
    });

    // A required field left empty, in the page's own words rather than the browser's.
    function missing(form) {
        var errors = null;
        form.querySelectorAll('[required]').forEach(function (el) {
            var slot = el.closest('.pf-field') && el.closest('.pf-field').querySelector('[data-err]');
            if (el.value.trim() || !slot) return;
            errors = errors || {};
            errors[slot.getAttribute('data-err')] = el.getAttribute('data-required') || 'Fill in this field.';
        });
        return errors;
    }

    document.querySelectorAll('[data-profile-form]').forEach(function (form) {
        form.noValidate = true;
        form.addEventListener('submit', async function (e) {
            e.preventDefault();
            clearErrors(form);
            var empty = missing(form);
            if (empty) { showErrors(form, empty); return; }
            if (!confirmed(form)) return;

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

// The profile photograph. The avatar opens it larger in a dialog that holds the actions.
// Cropped to a square and reduced here, so what is sent is a few tens of kilobytes
// whatever the camera produced, and the page reloads to show it in the rail as well.
(function () {
    var form = document.querySelector('.pf-photo');
    if (!form) return;

    var dialog = document.querySelector('.pf-photo-dialog');
    document.querySelector('[data-photo-open]').addEventListener('click', function () {
        error.textContent = '';
        dialog.showModal();
    });
    document.querySelector('[data-photo-close]').addEventListener('click', function () { dialog.close(); });
    // A click on the backdrop lands on the dialog itself, outside its form.
    dialog.addEventListener('click', function (e) { if (e.target === dialog) dialog.close(); });

    var EDGE = 384;
    var MAX_BYTES = 262144;
    var pick = form.querySelector('.pf-photo-pick');
    var error = document.querySelector('.pf-photo-error');
    var buttons = document.querySelectorAll('[data-photo-remove], [data-photo-close]');
    var change = document.querySelector('[data-photo-change]');
    var token = form.querySelector('input[name="__RequestVerificationToken"]').value;

    function busy(on) {
        buttons.forEach(function (b) { b.disabled = on; });
        pick.disabled = on;
        change.setAttribute('aria-disabled', on ? 'true' : 'false');
    }

    function fail(message) {
        error.textContent = message;
        busy(false);
    }

    function toBlob(canvas, quality) {
        return new Promise(function (resolve) { canvas.toBlob(resolve, 'image/jpeg', quality); });
    }

    async function square(file) {
        // Decoding through an image bitmap honours the orientation the camera recorded.
        var bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
        var side = Math.min(bitmap.width, bitmap.height);
        var edge = Math.min(EDGE, side);
        var canvas = document.createElement('canvas');
        canvas.width = canvas.height = edge;
        canvas.getContext('2d').drawImage(bitmap,
            (bitmap.width - side) / 2, (bitmap.height - side) / 2, side, side, 0, 0, edge, edge);
        bitmap.close();

        var blob = await toBlob(canvas, 0.85);
        if (blob && blob.size > MAX_BYTES) blob = await toBlob(canvas, 0.6);
        return blob;
    }

    async function post(url, body) {
        body.append('__RequestVerificationToken', token);
        var res = await fetch(url, { method: 'POST', body: body });
        if (res.ok) { location.reload(); return; }
        var answer = null;
        try { answer = await res.json(); } catch (x) { /* not JSON */ }
        fail((answer && answer.error) || 'The photo could not be saved. Try again.');
    }

    pick.addEventListener('change', async function () {
        var file = pick.files && pick.files[0];
        pick.value = '';
        if (!file) return;
        error.textContent = '';
        busy(true);
        try {
            var blob = await square(file);
            if (!blob || blob.size > MAX_BYTES) return fail('That photo could not be used. Try another.');
            var body = new FormData();
            body.append('photo', blob, 'photo.jpg');
            await post(form.getAttribute('action'), body);
        } catch (err) {
            fail('That photo could not be used. Try another.');
        }
    });

    var remove = document.querySelector('[data-photo-remove]');
    if (remove) remove.addEventListener('click', async function () {
        // The dialog already asks for intent, so a second tap is the confirmation.
        if (remove.dataset.armed !== '1') {
            remove.dataset.armed = '1';
            remove.textContent = 'Yes, remove';
            return;
        }
        error.textContent = '';
        busy(true);
        try { await post(form.dataset.remove, new FormData()); }
        catch (err) { fail('The photo could not be removed. Try again.'); }
    });
})();
