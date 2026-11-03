namespace FleetWise.Services
{
    // Who has a record open for editing, so a second person is turned away instead of
    // overwriting them. A hold lives 30 seconds past its last renewal, and the page renews
    // it only while it is open. A hold whose owner has not clicked or typed for 30 minutes
    // is not renewed, so an idle tab frees the record. Nothing outlives its owner:
    // leaving, a crashed browser and an ended session all stop the renewals.
    //
    // Kept in memory, which is correct only while the dashboard runs as a single instance.
    // A restart frees every hold. Running more than one instance needs the holds in a
    // database table instead.
    public class EditLocks
    {
        public static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

        public sealed record Hold(int UserId, string Name, DateTime Since, DateTime LastActive, DateTime Expires);

        private readonly Dictionary<string, Hold> _holds = new();

        // Takes or renews the hold. Returns null once it is the caller's, the other person's
        // hold when they have it, or the caller's own hold when it ended for being idle.
        // active: whether the caller clicked or typed since the last renewal.
        public Hold? Take(string key, int userId, string name, bool active, DateTime now)
        {
            lock (_holds)
            {
                foreach (var stale in _holds.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList())
                    _holds.Remove(stale);

                if (_holds.TryGetValue(key, out var h))
                {
                    if (h.UserId != userId) return h;
                    var last = active ? now : h.LastActive;
                    if (now - last >= Idle)
                    {
                        _holds.Remove(key);
                        return h;
                    }
                    _holds[key] = h with { LastActive = last, Expires = now + Lease };
                    return null;
                }
                _holds[key] = new Hold(userId, name, now, now, now + Lease);
                return null;
            }
        }

        public void Release(string key, int userId)
        {
            lock (_holds)
                if (_holds.TryGetValue(key, out var h) && h.UserId == userId) _holds.Remove(key);
        }

        // Someone else's live hold on the record, or null.
        public Hold? HeldByOther(string key, int userId, DateTime now)
        {
            lock (_holds)
                return _holds.TryGetValue(key, out var h) && h.Expires > now && h.UserId != userId ? h : null;
        }

        public static string Clock(DateTime t) =>
            t.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

        // The refusal a save gets when someone else has the record open.
        public static string Busy(Hold h) => $"Locked by {h.Name} since {Clock(h.Since)}.";
    }
}
