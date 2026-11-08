-- Expect: has_updated_at = true when any driver_availability row exists.
select (nav_badge_inputs(current_date, date_trunc('month', current_date)::date,
        (date_trunc('month', current_date) + interval '1 month')::date,
        array['Pending'], 'fast')::jsonb -> 'availability' -> 0) ? 'updated_at' as has_updated_at;
