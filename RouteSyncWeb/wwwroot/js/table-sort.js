// Column sorting for the dashboard's tables.
//
// A table opts in with data-sort. Every heading in it then sorts on a click, or on Enter
// or Space, first ascending and then flipping on each click after. A heading marked
// data-nosort, or with no text, is left alone.
//
// data-sort="client" (or just data-sort) sorts the rows already in the table. A cell
// sorts on its data-sort-value when it has one, and on its text otherwise: numbers as
// numbers, "Sep 28, 2026" style dates as dates, and anything else as text with the
// digits in it read as numbers, so V010 follows V009. A blank or a dash sorts last
// either way. The order is kept when the rows are replaced, as a search does.
//
// data-sort="server" is for a table that shows one page of a longer list, where only
// the server can sort the whole of it. The click marks the heading and raises
// "rs-sort" on the table with { key, dir }, taken from the heading's data-sort-key, and
// the page fetches the rows in that order.
(function () {
    'use strict';

    var MONTHS = { jan: 0, feb: 1, mar: 2, apr: 3, may: 4, jun: 5, jul: 6, aug: 7, sep: 8, oct: 9, nov: 10, dec: 11 };
    var DATE = /^([A-Za-z]{3})[a-z]* (\d{1,2}),? (\d{4})(?:,? (\d{1,2}):(\d{2})\s*([AaPp][Mm]))?$/;
    var NUMBER = /^[₱$]?\s*-?[\d,]*\.?\d+\s*(%|m|km|s|min|h|days?)?$/;

    function headings(table) {
        var row = table.tHead && table.tHead.rows[table.tHead.rows.length - 1];
        return row ? Array.prototype.slice.call(row.cells) : [];
    }

    function sortable(th) {
        return !th.hasAttribute('data-nosort') && th.textContent.trim() !== '';
    }

    // Headings take the keyboard and say what they do, once, whenever a table appears.
    function prepare(table) {
        headings(table).forEach(function (th) {
            if (!sortable(th) || th.classList.contains('rs-sortable')) return;
            th.classList.add('rs-sortable');
            th.tabIndex = 0;
            th.setAttribute('aria-sort', th.getAttribute('aria-sort') || 'none');
            th.title = th.title || 'Sort by ' + th.textContent.trim().toLowerCase();
        });
    }

    function keyOf(cell) {
        if (!cell) return { blank: true };
        var raw = cell.hasAttribute('data-sort-value')
            ? cell.getAttribute('data-sort-value')
            : cell.textContent.replace(/\s+/g, ' ').trim();
        if (raw === '' || raw === '—' || raw === '-' || raw === 'N/A') return { blank: true };

        if (NUMBER.test(raw)) {
            var n = parseFloat(raw.replace(/[^\d.\-]/g, ''));
            if (isFinite(n)) return { n: n };
        }

        var d = DATE.exec(raw);
        if (d && MONTHS[d[1].toLowerCase()] !== undefined) {
            var hour = d[4] ? (parseInt(d[4], 10) % 12) + (/p/i.test(d[6]) ? 12 : 0) : 0;
            return { n: new Date(+d[3], MONTHS[d[1].toLowerCase()], +d[2], hour, d[5] ? +d[5] : 0).getTime() };
        }

        return { s: raw };
    }

    var collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

    function compare(a, b) {
        if (a.n !== undefined && b.n !== undefined) return a.n - b.n;
        return collator.compare(a.s !== undefined ? a.s : String(a.n), b.s !== undefined ? b.s : String(b.n));
    }

    // Rows that stand for no record, such as "No users match." or a loading spinner, are
    // a single cell across the table and stay where they are.
    function records(tbody) {
        return Array.prototype.filter.call(tbody.rows, function (tr) {
            return !(tr.cells.length === 1 && tr.cells[0].colSpan > 1);
        });
    }

    function apply(table) {
        var col = +table.dataset.sortCol, dir = table.dataset.sortDir === 'desc' ? -1 : 1;
        if (isNaN(col)) return;

        Array.prototype.forEach.call(table.tBodies, function (tbody) {
            var rows = records(tbody);
            if (rows.length < 2) return;

            // Where each row stood, so rows that tie keep their order and a flip back
            // returns exactly what was there.
            var keyed = rows.map(function (tr, i) { return { tr: tr, k: keyOf(tr.cells[col]), i: i }; });
            keyed.sort(function (x, y) {
                if (x.k.blank !== y.k.blank) return x.k.blank ? 1 : -1;
                var c = x.k.blank ? 0 : compare(x.k, y.k) * dir;
                return c || x.i - y.i;
            });

            // Left alone when already in order, which is also what ends the round trip
            // through the observer below.
            if (keyed.every(function (x, i) { return x.tr === rows[i]; })) return;
            keyed.forEach(function (x) { tbody.appendChild(x.tr); });
        });
    }

    function mark(table, th, dir) {
        headings(table).forEach(function (h) {
            if (h.classList.contains('rs-sortable')) h.setAttribute('aria-sort', 'none');
        });
        th.setAttribute('aria-sort', dir === 'desc' ? 'descending' : 'ascending');
    }

    function sortBy(th) {
        var table = th.closest('table');
        if (!table || !table.hasAttribute('data-sort') || !sortable(th)) return;
        prepare(table);

        var col = headings(table).indexOf(th);
        var dir = +table.dataset.sortCol === col && table.dataset.sortDir === 'asc' ? 'desc' : 'asc';
        table.dataset.sortCol = col;
        table.dataset.sortDir = dir;
        mark(table, th, dir);

        if (table.getAttribute('data-sort') === 'server') {
            table.dispatchEvent(new CustomEvent('rs-sort', {
                detail: { key: th.getAttribute('data-sort-key') || th.textContent.trim().toLowerCase(), dir: dir },
            }));
        } else {
            apply(table);
        }
    }

    document.addEventListener('click', function (e) {
        var th = e.target.closest && e.target.closest('table[data-sort] thead th');
        if (th) sortBy(th);
    });

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Enter' && e.key !== ' ') return;
        var th = e.target.closest && e.target.closest('table[data-sort] thead th');
        if (!th) return;
        e.preventDefault();
        sortBy(th);
    });

    // Tables drawn later, and rows swapped in by a search or a refresh, are picked up
    // here: the first get their headings prepared, the second are put back in order.
    function watch() {
        document.querySelectorAll('table[data-sort]').forEach(prepare);

        new MutationObserver(function (changes) {
            var tables = new Set();
            changes.forEach(function (c) {
                var t = c.target.closest && c.target.closest('table[data-sort]');
                if (t) tables.add(t);
                c.addedNodes.forEach(function (n) {
                    if (n.nodeType !== 1) return;
                    if (n.matches('table[data-sort]')) tables.add(n);
                    n.querySelectorAll && n.querySelectorAll('table[data-sort]').forEach(function (x) { tables.add(x); });
                });
            });
            // New headings are a new set of columns, and the old choice no longer names one.
            changes.forEach(function (c) {
                var head = c.target.closest && c.target.closest('thead');
                var t = head && head.closest('table[data-sort]');
                if (t) { delete t.dataset.sortCol; delete t.dataset.sortDir; }
            });
            tables.forEach(function (t) {
                prepare(t);
                if (t.getAttribute('data-sort') !== 'server') apply(t);
            });
        }).observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', watch);
    else watch();
})();
