// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// Pushes an identifier to the right of the name it belongs to, in a column shared
// by every entry in the list.
//
// A native option cannot be laid out: no columns, no alignment, the browser draws
// that popup itself. The only thing under our control is the text, so the name is
// padded to the width of the longest one with a space that does not collapse. The
// selects that use this are set in a monospace face, without which every character
// is a different width and the column drifts.
window.rsIdLabels = function (rows) {
    var widest = 0;
    rows.forEach(function (r) { if (r.name.length > widest) widest = r.name.length; });
    return rows.map(function (r) {
        var pad = new Array(widest - r.name.length + 3).join('\u00A0');
        return r.name + pad + r.id;
    });
};

// Names carried by more than one person in a list. Everywhere else the identifier says
// nothing the name has not already said.
window.rsSharedNames = function (names) {
    var seen = {}, shared = {};
    names.forEach(function (n) {
        var k = (n || '').toLowerCase();
        if (seen[k]) shared[k] = true; else seen[k] = true;
    });
    return function (n) { return !!shared[(n || '').toLowerCase()]; };
};

// Lays a plain name over the closed control of a list built by rsIdLabels above.
//
// The padding that puts the identifiers in a column is only a column while the popup is
// open. Closed, the control draws one line of it, and a name followed by a run of spaces
// and a number is not what anybody wanted to read there. The face carries whatever the
// option's data-face says instead, and the control underneath is untouched: it still
// opens, still takes the keyboard, and still holds the value that gets submitted.
//
// Wrapping is done here rather than in the markup so that a list rebuilt by script keeps
// its face without every caller remembering to.
window.rsPickFace = function (sel) {
    if (!sel) return;

    var wrap = sel.parentElement;
    if (!wrap || !wrap.classList.contains('rs-pick')) {
        wrap = document.createElement('span');
        wrap.className = 'rs-pick';
        sel.parentNode.insertBefore(wrap, sel);
        wrap.appendChild(sel);

        var made = document.createElement('span');
        made.className = 'rs-pick-face';
        made.setAttribute('aria-hidden', 'true');
        wrap.appendChild(made);

        sel.addEventListener('change', function () { window.rsPickFace(sel); });
    }

    var face = wrap.querySelector('.rs-pick-face');
    var opt = sel.selectedOptions[0];
    face.textContent = (sel.value && opt && opt.dataset.face) ? opt.dataset.face : '';
};
