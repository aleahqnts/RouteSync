// A ranked list of drivers or buses to choose one from, the same on every screen that
// shows a ranking.
//
// Rows are labels around native radio buttons, so arrow keys, forms and screen readers
// treat the list as the radio group it is. Tiers are plain headings where the tier
// changes, the top pick carries a single Best match marker when it costs nothing, and a
// cost is an amber line under the name. The figures a candidate was ranked on sit in
// aligned columns, so they can be compared down the list.
//
// Every name and reason comes from the database and is set as text, never as markup.
(function () {
    'use strict';

    const TIERS = {
        driver: {
            1: 'Free all day',
            2: 'Has another shift that day',
            3: 'Would work 7 or more days in a row',
            4: 'Breaks a rest rule'
        },
        vehicle: {
            1: 'Based on this route',
            2: 'From another route',
            3: 'Needs attention'
        }
    };

    // The worst tier that costs nothing worth a warning. Matches SchedulingRules.NoCostTier.
    const NO_COST_TIER = 2;

    const COLUMNS = {
        driver: ['Route trips', 'This week'],
        vehicle: ['Seats', 'This week']
    };

    let nextId = 0;

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text != null) node.textContent = text;
        return node;
    }

    function plural(n, one, many) {
        return n + ' ' + (n === 1 ? one : many);
    }

    // The two figures a row shows, and the same figures as a phrase for a screen reader.
    function figuresOf(kind, item) {
        const f = item.facts;
        if (!f) return null;
        if (kind === 'vehicle') {
            return {
                values: [f.seats, f.weekTrips],
                spoken: plural(f.seats, 'seat', 'seats') + ', ' + plural(f.weekTrips, 'trip', 'trips') + ' this week'
            };
        }
        return {
            values: [f.routeTrips, f.weekShifts],
            spoken: plural(f.routeTrips, 'route trip', 'route trips') + ' in 30 days, '
                + plural(f.weekShifts, 'shift', 'shifts') + ' this week'
        };
    }

    // A fact worth saying that is not a cost: the other shift of a tier 2 driver, or where a
    // bus from another route is based and whether it seats as many.
    function noteOf(kind, item) {
        if (kind === 'driver') {
            return item.tier === 2 && item.warning ? item.warning : null;
        }
        const f = item.facts;
        if (!f) return null;
        const parts = [];
        if (!f.sameRoute) parts.push(f.homeRoute ? 'Based on ' + f.homeRoute : 'No home route');
        if (f.fewerSeatsThan) parts.push('Fewer seats than ' + f.fewerSeatsThan);
        return parts.length ? parts.join(', ') : null;
    }

    function costOf(kind, item) {
        if (item.onLeave) return item.reason || 'On approved leave';
        if (kind === 'driver') return item.tier > NO_COST_TIER ? item.warning : null;
        return item.warning || null;
    }

    /**
     * Builds a list inside host and returns a handle on it.
     *
     * options:
     *   kind       'driver' or 'vehicle'
     *   label      what the group is called for a screen reader, such as "Driver"
     *   items      ranked candidates: { id, name, sub, tier, rank, warning, reason, facts }
     *   onLeave    drivers left out for approved leave, listed last: { id, name, reason }
     *   current    what the trip has now, always first: { id, name, sub, problem }
     *   selected   the id chosen when the list opens
     *   shortfall  why nobody is free without a cost, or null
     *   limit      rows of the ranking shown before Show all; 3 when not given
     *   onChange   called with the chosen id
     */
    function create(host, options) {
        const kind = options.kind === 'vehicle' ? 'vehicle' : 'driver';
        const tiers = TIERS[kind];
        const limit = options.limit || 3;
        const items = options.items || [];
        const onLeave = (options.onLeave || []).map(function (d) { return Object.assign({ onLeave: true }, d); });
        const name = 'rsc-' + (++nextId);
        const top = items[0] && items[0].rank === 1 && items[0].tier <= NO_COST_TIER ? items[0] : null;

        let expanded = false;
        let chosen = options.selected != null ? String(options.selected) : null;

        const root = el('div', 'rsc');
        root.dataset.kind = kind;

        const live = el('div', 'rsc-sr');
        live.setAttribute('aria-live', 'polite');

        if (options.shortfall) {
            const line = el('p', 'rsc-shortfall');
            const icon = el('i', 'ti ti-alert-triangle');
            icon.setAttribute('aria-hidden', 'true');
            line.append(icon, el('span', null, options.shortfall));
            root.appendChild(line);
        }

        const head = el('div', 'rsc-head');
        head.setAttribute('aria-hidden', 'true');
        head.append(el('span'), el('span'), el('span', null, COLUMNS[kind][0]), el('span', null, COLUMNS[kind][1]));

        const group = el('div', 'rsc-list');
        group.setAttribute('role', 'radiogroup');
        if (options.label) group.setAttribute('aria-label', options.label);

        const more = el('button', 'rsc-more');
        more.type = 'button';

        const foot = el('p', 'rsc-foot', 'Best match is suggested by the scheduling rules.');
        foot.hidden = !top;

        root.append(head, group, more, foot, live);
        host.replaceChildren(root);

        function row(item, extraNote) {
            const id = String(item.id);
            const label = el('label', 'rsc-row');
            label.dataset.id = id;

            const radio = el('input', 'rsc-radio');
            radio.type = 'radio';
            radio.name = name;
            radio.value = id;
            radio.checked = id === chosen;

            const body = el('span', 'rsc-body');
            const line = el('span', 'rsc-line');
            const nameEl = el('span', 'rsc-name', item.name);
            nameEl.id = name + '-n-' + id;
            line.appendChild(nameEl);
            let subEl = null;
            if (item.sub) {
                subEl = el('span', 'rsc-sub', item.sub);
                subEl.id = name + '-s-' + id;
                line.appendChild(subEl);
            }
            if (top && !item.onLeave && String(top.id) === id) line.appendChild(el('span', 'rsc-best', 'Best match'));
            body.appendChild(line);

            const described = [];
            const note = extraNote || (item.onLeave ? null : noteOf(kind, item));
            if (note) {
                const n = el('span', 'rsc-note', note);
                n.id = name + '-d-' + id;
                described.push(n.id);
                body.appendChild(n);
            }

            const cost = item.problem || costOf(kind, item);
            if (cost) {
                const c = el('span', 'rsc-cost');
                c.id = name + '-c-' + id;
                const icon = el('i', 'ti ti-alert-triangle');
                icon.setAttribute('aria-hidden', 'true');
                c.append(icon, el('span', null, cost));
                described.push(c.id);
                body.appendChild(c);
            }

            label.append(radio, body);

            const figures = item.onLeave || item.isCurrent ? null : figuresOf(kind, item);
            [0, 1].forEach(function (i) {
                const cell = el('span', 'rsc-fig', figures ? String(figures.values[i]) : '');
                cell.setAttribute('aria-hidden', 'true');
                label.appendChild(cell);
            });

            // Named by the name and the figures; the note and any cost describe it, so a
            // screen reader hears the cost before the choice is made.
            const labelled = subEl ? [nameEl.id, subEl.id] : [nameEl.id];
            if (figures) {
                const spoken = el('span', 'rsc-sr', figures.spoken);
                spoken.id = name + '-f-' + id;
                label.appendChild(spoken);
                labelled.push(spoken.id);
            }
            radio.setAttribute('aria-labelledby', labelled.join(' '));
            if (described.length) radio.setAttribute('aria-describedby', described.join(' '));

            label.classList.toggle('rsc-row--selected', radio.checked);
            radio.addEventListener('change', function () {
                chosen = id;
                group.querySelectorAll('.rsc-row').forEach(function (r) {
                    r.classList.toggle('rsc-row--selected', r.dataset.id === id);
                });
                if (options.onChange) options.onChange(id);
            });

            return label;
        }

        function render() {
            group.replaceChildren();

            if (options.current) {
                group.appendChild(el('div', 'rsc-tier', 'Current'));
                group.appendChild(row(Object.assign({ isCurrent: true }, options.current)));
            }

            // Collapsed, the ranking shows its first rows, and the chosen row too wherever it
            // sits, so what is selected is never out of sight.
            const shown = expanded
                ? items
                : items.filter(function (c, i) { return i < limit || String(c.id) === chosen; });

            let lastTier = null;
            shown.forEach(function (c) {
                if (c.tier !== lastTier) {
                    group.appendChild(el('div', 'rsc-tier', tiers[c.tier] || 'Other'));
                    lastTier = c.tier;
                }
                group.appendChild(row(c));
            });

            const leaveShown = expanded
                ? onLeave
                : onLeave.filter(function (d) { return String(d.id) === chosen; });
            if (leaveShown.length) {
                group.appendChild(el('div', 'rsc-tier', 'On approved leave'));
                leaveShown.forEach(function (d) { group.appendChild(row(d)); });
            }

            // Offered only when the short list leaves someone out; once open, it folds back.
            const total = items.length + onLeave.length;
            more.hidden = collapsedCount() >= total;
            more.textContent = expanded ? 'Show fewer' : 'Show all ' + total;
            more.setAttribute('aria-expanded', expanded ? 'true' : 'false');

            head.hidden = items.length === 0;
        }

        // How many rows the short list shows: the first few, and the chosen one wherever it is.
        function collapsedCount() {
            return items.filter(function (c, i) { return i < limit || String(c.id) === chosen; }).length
                + onLeave.filter(function (d) { return String(d.id) === chosen; }).length;
        }

        more.addEventListener('click', function () {
            const total = items.length + onLeave.length;
            expanded = !expanded;
            render();

            if (expanded) {
                live.textContent = 'Showing all ' + total;
                const selected = group.querySelector('.rsc-radio:checked') || group.querySelector('.rsc-radio');
                if (selected) selected.focus();
            } else {
                live.textContent = 'Showing ' + collapsedCount() + ' of ' + total;
                more.focus();
            }
        });

        render();

        return {
            element: root,

            /** The chosen id, or an empty string when nothing is chosen. */
            value: function () { return chosen || ''; },

            /** Moves focus to the chosen row, or the first one. */
            focus: function () {
                const target = group.querySelector('.rsc-radio:checked') || group.querySelector('.rsc-radio');
                if (target) target.focus();
            },

            /** Says something to a screen reader without moving focus. */
            announce: function (text) { live.textContent = text; }
        };
    }

    window.RsCandidateList = { create: create, NO_COST_TIER: NO_COST_TIER };
})();
