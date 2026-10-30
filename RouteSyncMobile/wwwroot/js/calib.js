// Remote line calibration editor. The drag loop lives entirely in JS (the WebView
// JS->.NET bridge is too slow for per-move events, as in ptr.js) and redraws the SVG
// directly; .NET gets the normalized line only on drag end, through OnLineChanged.
// Coordinate contract: the snapshot is the camera's display-space frame and the <img>
// is sized to the stage, so normalizing against the stage rect is the same as
// normalizing against the frame.
window.calib = (function () {
    let ref = null, stage = null, svg = null;
    let ax = 0.5, ay = 0.05, bx = 0.5, by = 0.95, sign = 1;
    let dragging = -1; // 0 = handle A, 1 = handle B
    let ro = null;

    function setAttrs(id, attrs) {
        const el = document.getElementById(id);
        if (!el) return;
        for (const k in attrs) el.setAttribute(k, attrs[k]);
    }

    function redraw() {
        if (!svg || !stage) return;
        const r = stage.getBoundingClientRect();
        const w = Math.max(1, r.width), h = Math.max(1, r.height);
        // Pixel-space viewBox: circles stay round (a 0..1 viewBox would stretch them).
        svg.setAttribute('viewBox', `0 0 ${w} ${h}`);
        const AX = ax * w, AY = ay * h, BX = bx * w, BY = by * h;
        setAttrs('cl-ln', { x1: AX, y1: AY, x2: BX, y2: BY });
        setAttrs('cl-hao', { cx: AX, cy: AY }); setAttrs('cl-ha', { cx: AX, cy: AY });
        setAttrs('cl-hbo', { cx: BX, cy: BY }); setAttrs('cl-hb', { cx: BX, cy: BY });
        // Boarding-side arrow: perpendicular at the midpoint (mirrors CameraScreen).
        const mx = (AX + BX) / 2, my = (AY + BY) / 2;
        let ndx = -(BY - AY), ndy = (BX - AX);
        const len = Math.max(1, Math.hypot(ndx, ndy));
        ndx = ndx / len * sign; ndy = ndy / len * sign;
        const tx = mx + ndx * 40, ty = my + ndy * 40;
        setAttrs('cl-ar', { x1: mx, y1: my, x2: tx, y2: ty });
        setAttrs('cl-art', { cx: tx, cy: ty });
    }

    function norm(e) {
        const r = stage.getBoundingClientRect();
        return [
            Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)),
            Math.min(1, Math.max(0, (e.clientY - r.top) / r.height))
        ];
    }

    function down(e) {
        const [nx, ny] = norm(e);
        const da = (nx - ax) ** 2 + (ny - ay) ** 2;
        const db = (nx - bx) ** 2 + (ny - by) ** 2;
        dragging = da <= db ? 0 : 1;
        try { stage.setPointerCapture(e.pointerId); } catch { }
        move(e);
    }

    function move(e) {
        if (dragging < 0) return;
        const [nx, ny] = norm(e);
        if (dragging === 0) { ax = nx; ay = ny; } else { bx = nx; by = ny; }
        redraw();
        e.preventDefault();
    }

    function up() {
        if (dragging < 0) return;
        dragging = -1;
        if (ref) ref.invokeMethodAsync('OnLineChanged', ax, ay, bx, by);
    }

    return {
        init: function (dotnetRef) {
            ref = dotnetRef;
            stage = document.getElementById('calib-stage');
            svg = document.getElementById('calib-svg');
            if (!stage || !svg) return;
            stage.addEventListener('pointerdown', down);
            stage.addEventListener('pointermove', move);
            stage.addEventListener('pointerup', up);
            stage.addEventListener('pointercancel', up);
            if (ro) ro.disconnect();
            ro = new ResizeObserver(redraw);
            ro.observe(stage);
            redraw();
        },
        set: function (a, b, c, d, s) { ax = a; ay = b; bx = c; by = d; sign = s; redraw(); },
        dispose: function () { if (ro) ro.disconnect(); ro = null; ref = null; }
    };
})();
