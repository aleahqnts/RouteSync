// Loaded in the head, ahead of every page script, because pages stamp their "Updated"
// time as soon as their own script runs, which is before the scripts at the foot of
// the layout have loaded.

// Times as the dashboard writes them everywhere: on the Philippine clock whatever zone
// the browser is in, and in the server's style, "Oct 3, 2026 6:05 AM". The server
// writes the same with "MMM d, yyyy h:mm tt", so a time read on one page matches the
// same moment read on another.
//
//   rsTime.now()          the time now, "6:05 AM", for a page's "Updated" stamp
//   rsTime.instant(date)  a moment, given as a Date
//   rsTime.wall(text)     a time sent as Philippine clock digits with no zone, such as
//                         "2026-10-03T06:05:00", written as they read: never moved by
//                         the browser's own zone
window.rsTime = (function () {
    var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    var manila = new Intl.DateTimeFormat('en-US', {
        timeZone: 'Asia/Manila', year: 'numeric', month: 'numeric', day: 'numeric',
        hour: 'numeric', minute: 'numeric', hourCycle: 'h23'
    });

    function write(y, mo, d, h, mi) {
        var clock = (h % 12 || 12) + ':' + String(mi).padStart(2, '0') + (h < 12 ? ' AM' : ' PM');
        return { date: MONTHS[mo] + ' ' + d + ', ' + y, time: clock };
    }

    function ofInstant(dt) {
        var p = {};
        manila.formatToParts(dt).forEach(function (x) { p[x.type] = +x.value; });
        return write(p.year, p.month - 1, p.day, p.hour % 24, p.minute);
    }

    return {
        now: function () { return ofInstant(new Date()).time; },
        instant: function (dt) { var w = ofInstant(dt); return w.date + ' ' + w.time; },
        wall: function (text) {
            var m = /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})/.exec(text || '');
            if (!m) return '';
            var w = write(+m[1], +m[2] - 1, +m[3], +m[4], +m[5]);
            return w.date + ' ' + w.time;
        }
    };
})();
