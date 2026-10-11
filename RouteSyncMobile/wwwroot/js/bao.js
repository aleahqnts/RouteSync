// Poking Bao. A tap on any of his drawings shoves him away from the side it landed on,
// and he glances toward it. The pages that show him are
// rendered by Blazor, so one listener on the document covers every one of them.
(function () {
    // How far a pupil moves, in drawing units, and how long he holds the glance.
    var LOOK = 22, HOLD_MS = 1200;

    document.addEventListener('click', function (e) {
        var svg = e.target.closest && e.target.closest('svg.bao');
        if (!svg) return;

        var r = svg.getBoundingClientRect();
        svg.style.setProperty('--bao-shove', e.clientX < r.left + r.width / 2 ? 1 : -1);
        svg.classList.remove('bao--poke');
        svg.classList.add('bao--poke');
        setTimeout(function () { svg.classList.remove('bao--poke'); }, 650);

        var pupils = svg.querySelectorAll('.bao-pupil');
        pupils.forEach(function (p) {
            var r = p.parentNode.getBoundingClientRect();
            var dx = e.clientX - (r.left + r.width / 2), dy = e.clientY - (r.top + r.height / 2);
            var d = Math.hypot(dx, dy) || 1;
            p.style.transform = 'translate(' + (dx / d * LOOK).toFixed(1) + 'px,' + (dy / d * LOOK).toFixed(1) + 'px)';
        });
        clearTimeout(svg._baoLook);
        svg._baoLook = setTimeout(function () {
            pupils.forEach(function (p) { p.style.transform = ''; });
        }, HOLD_MS);
    });
})();
