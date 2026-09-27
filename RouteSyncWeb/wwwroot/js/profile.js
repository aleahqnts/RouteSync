// The signed-in person's own profile, opened from their name on the rail or in the sheet.
//
// The account is read when the dialog opens rather than drawn into every page, so a page
// nobody opens it on costs nothing. Saving writes the name and, when all three password
// fields are filled, the password; the server checks everything again and answers with
// a message per field.
(function () {
    var dialog = document.getElementById('fwProfile');
    if (!dialog) return;

    var form = document.getElementById('fwProfileForm');
    var loading = dialog.querySelector('[data-profile-loading]');
    var content = dialog.querySelector('[data-profile-content]');
    var save = dialog.querySelector('[data-profile-save]');
    var opener = null;

    function field(name) { return form.elements[name]; }

    function clearErrors() {
        dialog.querySelectorAll('[data-err]').forEach(function (el) { el.textContent = ''; });
        form.querySelectorAll('.fw-profile__field--bad').forEach(function (el) {
            el.classList.remove('fw-profile__field--bad');
        });
    }

    function showErrors(errors) {
        Object.keys(errors || {}).forEach(function (key) {
            var slot = dialog.querySelector('[data-err="' + key + '"]');
            if (!slot) slot = dialog.querySelector('[data-err="form"]');
            slot.textContent = errors[key];
            var wrap = slot.closest('.fw-profile__field');
            if (wrap) wrap.classList.add('fw-profile__field--bad');
        });
        var first = form.querySelector('.fw-profile__field--bad input');
        if (first) first.focus();
    }

    async function open(e) {
        opener = e && e.currentTarget;
        clearErrors();
        form.reset();
        loading.textContent = 'Loading your profile…';
        loading.hidden = false;
        content.hidden = true;
        save.disabled = true;
        dialog.classList.add('fw-profile--open');

        try {
            var res = await fetch('/Profile/Details', { headers: { 'Accept': 'application/json' } });
            if (!res.ok) throw new Error();
            var me = await res.json();

            field('FirstName').value = me.firstName;
            field('MiddleName').value = me.middleName;
            field('LastName').value = me.lastName;
            dialog.querySelector('[data-profile-email]').textContent = me.email;
            dialog.querySelector('[data-profile-role]').textContent = me.role;

            loading.hidden = true;
            content.hidden = false;
            save.disabled = false;
            field('FirstName').focus();
        } catch (err) {
            loading.textContent = 'Your profile could not be loaded. Close this and try again.';
        }
    }

    function close() {
        dialog.classList.remove('fw-profile--open');
        if (opener && opener.focus) opener.focus();
    }

    form.addEventListener('submit', async function (e) {
        e.preventDefault();
        clearErrors();
        save.disabled = true;
        save.textContent = 'Saving…';

        try {
            var res = await fetch('/Profile/Update', { method: 'POST', body: new FormData(form) });
            var body = null;
            try { body = await res.json(); } catch (x) { /* not JSON */ }

            if (!res.ok) {
                showErrors((body && body.errors) || { form: 'Your changes could not be saved. Try again.' });
                return;
            }

            // The rail and the sheet both name the person. The rest of the page is drawn
            // from the account on the next load, which the server reads fresh.
            document.querySelectorAll('.fw-sidebar__profile-name, .fw-sheet__name').forEach(function (el) {
                el.textContent = body.name;
            });
            close();
        } catch (err) {
            showErrors({ form: 'Your changes could not be saved. Check your connection and try again.' });
        } finally {
            save.disabled = false;
            save.textContent = 'Save changes';
        }
    });

    document.querySelectorAll('[data-profile-open]').forEach(function (btn) {
        btn.addEventListener('click', open);
    });
    dialog.querySelectorAll('[data-profile-close]').forEach(function (btn) {
        btn.addEventListener('click', close);
    });
    dialog.addEventListener('click', function (e) { if (e.target === dialog) close(); });

    // Named for dialog-stack.js, which closes the frontmost dialog on Escape.
    window.fwCloseProfile = close;
})();
