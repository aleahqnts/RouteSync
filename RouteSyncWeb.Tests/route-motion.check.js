// Checks the Fleet Map's marker motion (RouteSyncWeb/wwwroot/js/route-motion.js) by
// driving a simulated bus through it, frame by frame, with no map or browser.
//
//   node RouteSyncWeb.Tests/route-motion.check.js
//
// Prints "pass" or stops at the first failed check.
'use strict';

const assert = require('assert');
const path = require('path');

global.window = {};
require(path.join(__dirname, '..', 'RouteSyncWeb', 'wwwroot', 'js', 'route-motion.js'));
const { measure, follow, pace } = window.RouteMotion;

// A road about 2 km due east.
const line = measure([[14.55, 121.04], [14.55, 121.0585]]);
assert.ok(line.length > 1900 && line.length < 2100, 'road measured at ' + line.length);

const FRAME = 1 / 60;

// Runs the marker from start to end seconds. truth(t) is where the bus really is;
// readings arrive every `every` seconds, `lag` seconds after the fix, until `quietAfter`.
function ride({ truth, speedAt, end, every = 5, lag = 1.5, quietAfter = Infinity, wander = () => 0 }) {
    let shown = truth(0), anchor = truth(0), speed = speedAt(0), seenAt = 0, lastHeard = 0, lagMs = lag * 1000;
    const frames = [];
    for (let t = 0; t <= end; t += FRAME) {
        const fixAt = Math.floor((t - lag) / every) * every;
        if (fixAt >= 0 && fixAt < quietAfter && fixAt + lag > lastHeard + 1e-9 && fixAt + lag <= t) {
            // The server never moves a bus back for wander: a reading behind the last
            // repeats the last place, which is news to the marker only about a bus that
            // was standing.
            const place = truth(fixAt) + wander(fixAt);
            const wasMoving = speed >= 1;
            speed = speedAt(fixAt);
            if (place > anchor || !wasMoving) {
                anchor = Math.max(anchor, place);
                seenAt = fixAt + lag;
            }
            lastHeard = fixAt + lag;
        }
        const next = follow(line, shown, anchor, speed, (t - seenAt) * 1000 + lagMs, FRAME);
        frames.push({ t, shown: next.along, was: shown, truth: truth(t) });
        shown = next.along;
    }
    return frames;
}

// Never backwards, and never faster than twice the bus's speed or a slow walk.
function steady(frames, fastest) {
    for (const f of frames) {
        assert.ok(f.shown >= f.was - 1e-9, `went back at ${f.t.toFixed(2)} s`);
        assert.ok((f.shown - f.was) / FRAME <= fastest + 1e-6,
            `ran at ${((f.shown - f.was) / FRAME).toFixed(1)} m/s at ${f.t.toFixed(2)} s`);
    }
}

// A bus at a steady 5 m/s, its fixes wandering up to 8 m either way.
{
    const frames = ride({
        truth: t => 100 + 5 * t,
        speedAt: () => 5,
        end: 120,
        wander: t => 8 * Math.sin(t * 1.7)
    });
    steady(frames, 10);
    for (const f of frames.filter(f => f.t > 15))
        assert.ok(Math.abs(f.shown - f.truth) < 20, `${(f.shown - f.truth).toFixed(1)} m off at ${f.t.toFixed(1)} s`);
    // Moving continuously: no stretch of a second without progress.
    for (let i = 60; i < frames.length; i += 60)
        assert.ok(frames[i].shown - frames[i - 60].shown > 2, `stalled before ${frames[i].t.toFixed(1)} s`);
}

// A bus that brakes to a stop at a light at 60 s and pulls away at 90 s.
{
    const truth = t => t < 60 ? 5 * t : t < 90 ? 300 : 300 + 5 * (t - 90);
    const speedAt = t => t < 60 ? 5 : t < 90 ? 0 : 5;
    const frames = ride({ truth, speedAt, end: 150, wander: t => 8 * Math.sin(t * 1.7) });
    steady(frames, 10);
    for (const f of frames.filter(f => f.t > 90))
        assert.ok(f.shown - f.truth < 20, `ran ${(f.shown - f.truth).toFixed(1)} m ahead pulling away at ${f.t.toFixed(1)} s`);
    const waiting = frames.filter(f => f.t > 70 && f.t < 90);
    assert.ok(waiting.every(f => f.shown <= 300 + 35), 'ran on past the light by ' + Math.max(...waiting.map(f => f.shown - 300)).toFixed(1) + ' m');
    assert.ok(waiting[waiting.length - 1].shown - waiting[0].shown < 1, 'crept while the bus stood');
}

// A bus that goes quiet at 30 s, in a dead zone: the marker stops within ten seconds'
// travel of its last news rather than driving on indefinitely.
{
    const frames = ride({ truth: t => 5 * t, speedAt: () => 5, end: 90, quietAfter: 30 });
    steady(frames, 10);
    const last = frames[frames.length - 1].shown;
    assert.ok(last <= 25 * 5 + 5 * 12, 'drove on to ' + last.toFixed(1) + ' m');
    assert.ok(frames.filter(f => f.t > 60).every(f => f.shown === last), 'still moving after news stopped');
}

// Catching up a reading far ahead goes no faster than twice the bus's speed.
assert.strictEqual(pace(500, 5), 10);
// A standing bus whose reading moved on closes the gap at a walk at most.
assert.strictEqual(pace(500, 0), 4);
// Ahead of the bus, the marker waits rather than reversing.
assert.strictEqual(pace(-50, 5), 0);

console.log('pass');
