// Phase 8e: remote camera calibration (admin). Same coordinate contract as the
// driver app's editor (REMOTE-CONTROL-plan.md §2): the snapshot IS the camera's
// display-space frame, the <img> sizes the stage, so normalizing drag positions
// against the stage rect gives exactly the 0..1 coords the camera's line-cross
// counter consumes. Service key never reaches this file: every call goes through
// the VehiclesController proxy endpoints (camUrls, injected by Index.cshtml).
//
// window.camUrls = { state, wake, snapshot, save } set before this runs.
(function () {
    'use strict';

    let vehicleId = null, deviceId = null;
    let ax = 0.5, ay = 0.05, bx = 0.5, by = 0.95, sign = 1, useBack = false, version = 0;
    let dragging = -1;
    let wakeSentAt = 0;
    let statusTimer = null, pollAbort = false;
    let busy = false;
    let ro = null;

    const $ = (id) => document.getElementById(id);

    function token() {
        const el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : '';
    }

    async function post(url, params) {
        const res = await fetch(url, {
            method: 'POST',
            headers: { 'RequestVerificationToken': token(), 'Content-Type': 'application/x-www-form-urlencoded' },
            body: new URLSearchParams(params)
        });
        return res;
    }

    // ── SVG editor (same drag logic as the driver app's calib.js) ──

    function setAttrs(id, attrs) {
        const el = $(id);
        if (!el) return;
        for (const k in attrs) el.setAttribute(k, attrs[k]);
    }

    function redraw() {
        const frame = $('camFrame'), svg = $('camSvg');
        if (!frame || !svg) return;
        const r = frame.getBoundingClientRect();
        const w = Math.max(1, r.width), h = Math.max(1, r.height);
        svg.setAttribute('viewBox', '0 0 ' + w + ' ' + h);
        const AX = ax * w, AY = ay * h, BX = bx * w, BY = by * h;
        setAttrs('cam-ln', { x1: AX, y1: AY, x2: BX, y2: BY });
        setAttrs('cam-hao', { cx: AX, cy: AY }); setAttrs('cam-ha', { cx: AX, cy: AY });
        setAttrs('cam-hbo', { cx: BX, cy: BY }); setAttrs('cam-hb', { cx: BX, cy: BY });
        const mx = (AX + BX) / 2, my = (AY + BY) / 2;
        let ndx = -(BY - AY), ndy = (BX - AX);
        const len = Math.max(1, Math.hypot(ndx, ndy));
        ndx = ndx / len * sign; ndy = ndy / len * sign;
        const tx = mx + ndx * 40, ty = my + ndy * 40;
        setAttrs('cam-ar', { x1: mx, y1: my, x2: tx, y2: ty });
        setAttrs('cam-art', { cx: tx, cy: ty });
    }

    function norm(e) {
        const r = $('camFrame').getBoundingClientRect();
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
        try { $('camFrame').setPointerCapture(e.pointerId); } catch { }
        move(e);
    }

    function move(e) {
        if (dragging < 0) return;
        const [nx, ny] = norm(e);
        if (dragging === 0) { ax = nx; ay = ny; } else { bx = nx; by = ny; }
        clearApplied();
        redraw();
        e.preventDefault();
    }

    function up() { dragging = -1; }

    function wireStage() {
        const frame = $('camFrame');
        if (frame.__camWired) return;
        frame.__camWired = true;
        frame.addEventListener('pointerdown', down);
        frame.addEventListener('pointermove', move);
        frame.addEventListener('pointerup', up);
        frame.addEventListener('pointercancel', up);
        ro = new ResizeObserver(redraw);
        ro.observe(frame);
    }

    // ── Panel state / notes ──

    function note(text, cls) {
        const el = $('camNote');
        el.textContent = text || '';
        el.className = 'cam-note' + (cls ? ' cam-note--' + cls : '');
        el.style.display = text ? 'block' : 'none';
    }

    function clearApplied() {
        const el = $('camNote');
        if (el.classList.contains('cam-note--ok')) note('');
    }

    function setBusy(b) {
        busy = b;
        ['camFlipSide', 'camFlipLens', 'camRefresh', 'camSave'].forEach(id => { $(id).disabled = b; });
    }

    function showWait(text) {
        $('camWait').style.display = 'flex';
        $('camEditor').style.display = 'none';
        $('camWaitText').textContent = text;
        $('camRetry').style.display = 'none';
    }

    function showError(text) {
        showWait(text);
        $('camRetry').style.display = 'inline-block';
    }

    function chip(online) {
        const el = $('camChip');
        el.textContent = online ? 'Online' : 'Offline';
        el.className = 'cam-chip ' + (online ? 'cam-chip--on' : 'cam-chip--off');
    }

    async function getState() {
        const res = await fetch(window.camUrls.state + '?vehicleId=' + encodeURIComponent(vehicleId));
        if (!res.ok) return null;
        return res.json();
    }

    // Counts the camera holds that the database has not confirmed storing. A phone
    // that lost its trip claim while it was out of contact is refused on every retry,
    // so this can be a number that never falls on its own.
    function backlog(n, oldest) {
        const el = $('camBacklog');
        if (!el) return;
        if (!n) { el.style.display = 'none'; return; }
        let text = n === 1
            ? 'This camera is holding 1 trip count it has not been able to send.'
            : 'This camera is holding ' + n + ' trip counts it has not been able to send.';
        if (oldest) {
            const days = Math.floor((Date.now() - Date.parse(oldest)) / 86400000);
            if (days >= 1) text += ' The oldest is ' + days + (days === 1 ? ' day' : ' days') + ' old.';
        }
        el.textContent = text;
        el.style.display = 'block';
    }

    function applyState(s) {
        if (!s) return;
        if (s.status && s.status.last_seen) {
            chip(Date.now() - Date.parse(s.status.last_seen) < 30000);
        } else {
            chip(false);
        }
        backlog(s.status && s.status.unreconciled_counts,
                s.status && s.status.unreconciled_oldest_at);
    }

    // ── Flows ──

    async function waitForSnapshot() {
        // ~30s: wake + AE warm-up + upload. 5s clock-skew slack on the compare.
        for (let i = 0; i < 20 && !pollAbort; i++) {
            await new Promise(r => setTimeout(r, 1500));
            try {
                const s = await getState();
                applyState(s);
                if (s && s.status && s.status.snapshot_ready_at &&
                    Date.parse(s.status.snapshot_ready_at) >= wakeSentAt - 5000) {
                    return true;
                }
            } catch { }
        }
        return false;
    }

    function loadImage() {
        return new Promise((resolve) => {
            const img = $('camImg');
            img.onload = () => resolve(true);
            img.onerror = () => resolve(false);
            img.src = window.camUrls.snapshot + '?deviceId=' + encodeURIComponent(deviceId) + '&t=' + Date.now();
        });
    }

    window.camStart = async function () {
        pollAbort = false;
        showWait('Waking the camera…');
        try {
            const s = await getState();
            if (!s || !s.deviceId) { showError('No counter phone is bound to this bus.'); return; }
            deviceId = s.deviceId;
            applyState(s);
            if (s.config) {
                ax = s.config.line_ax ?? 0.5; ay = s.config.line_ay ?? 0.05;
                bx = s.config.line_bx ?? 0.5; by = s.config.line_by ?? 0.95;
                sign = s.config.inward_sign || 1;
                useBack = !!s.config.use_back_camera;
                version = s.config.version || 0;
            }
            wakeSentAt = Date.now();
            const w = await post(window.camUrls.wake, { deviceId: deviceId });
            if (!w.ok) { showError("Can't reach the camera service. Try again."); return; }
            if (!(await waitForSnapshot())) {
                showError("The camera didn't respond. Is the counter phone on and connected?");
                return;
            }
            if (!(await loadImage())) { showError("Couldn't load the photo. Try again."); return; }
            $('camWait').style.display = 'none';
            $('camEditor').style.display = 'block';
            note('');
            wireStage();
            redraw();
        } catch {
            showError('Something went wrong. Try again.');
        }
    };

    window.camOpen = function (vId) {
        vehicleId = vId;
        $('camOverlay').style.display = 'flex';
        chip(false);
        // Live chip while the panel is open.
        statusTimer = setInterval(async () => { try { applyState(await getState()); } catch { } }, 5000);
        window.camStart();
    };

    window.camClose = function () {
        pollAbort = true;
        clearInterval(statusTimer);
        if (ro) { ro.disconnect(); ro = null; }
        $('camFrame').__camWired = false;
        $('camOverlay').style.display = 'none';
        // Camera purges its snapshot on its own ~2 min timeout; nothing to clean here.
    };

    window.camFlipSide = function () {
        sign = -sign;
        clearApplied();
        redraw();
    };

    window.camRefresh = async function () {
        setBusy(true); $('camVeil').style.display = 'flex'; note('');
        try {
            wakeSentAt = Date.now();
            const w = await post(window.camUrls.wake, { deviceId: deviceId });
            if (w.ok && (await waitForSnapshot()) && (await loadImage())) {
                redraw();
            } else {
                note("Couldn't get a new photo. The camera didn't respond.", 'err');
            }
        } catch {
            note("Couldn't get a new photo. Check the connection.", 'err');
        }
        $('camVeil').style.display = 'none'; setBusy(false);
    };

    // Lens swap: save the lens (full config write, same as the driver app), wait for
    // the camera to echo the version so the next photo really uses the new lens.
    window.camFlipLens = async function () {
        setBusy(true); $('camVeil').style.display = 'flex'; note('');
        try {
            useBack = !useBack;
            const saved = await saveConfig();
            if (!saved) { note("Couldn't flip the camera. Try again.", 'err'); }
            else {
                await waitForApplied(saved.version);
                await window.camRefresh._inner();
            }
        } catch {
            note("Couldn't flip the camera. Check the connection.", 'err');
        }
        $('camVeil').style.display = 'none'; setBusy(false);
    };

    // Refresh body reusable without the busy/veil wrapper (camFlipLens runs its own).
    window.camRefresh._inner = async function () {
        wakeSentAt = Date.now();
        const w = await post(window.camUrls.wake, { deviceId: deviceId });
        if (w.ok && (await waitForSnapshot()) && (await loadImage())) redraw();
        else note("Camera switched, but no new photo arrived. Tap Refresh.", 'err');
    };

    async function saveConfig() {
        const res = await post(window.camUrls.save, {
            deviceId: deviceId,
            ax: ax.toFixed(5), ay: ay.toFixed(5),
            bx: bx.toFixed(5), by: by.toFixed(5),
            inwardSign: sign,
            useBack: useBack
        });
        if (!res.ok) return null;
        const data = await res.json();
        version = data.version;
        return data;
    }

    async function waitForApplied(v) {
        for (let i = 0; i < 8; i++) {
            await new Promise(r => setTimeout(r, 1500));
            try {
                const s = await getState();
                applyState(s);
                if (s && s.status && s.status.config_version_applied >= v) return true;
            } catch { }
        }
        return false;
    }

    window.camSave = async function () {
        setBusy(true); note('Saving…');
        try {
            const saved = await saveConfig();
            if (!saved) { note("Couldn't save. Try again.", 'err'); setBusy(false); return; }
            if (await waitForApplied(saved.version)) {
                note('✓ Camera adopted the new line.', 'ok');
            } else {
                note('Saved. The camera will apply it as soon as it reconnects.');
            }
        } catch {
            note("Couldn't save. Check the connection.", 'err');
        }
        setBusy(false);
    };
})();
