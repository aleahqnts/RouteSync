using FleetWiseMobile.Models;
using Microsoft.Maui.Storage;

namespace FleetWiseMobile.Services;

/// <summary>
/// Background poller for driver messages, running on its own timer for the whole
/// signed-in session regardless of which page is shown.
/// </summary>
/// <remarks>
/// Drives three things: the unread badge through <see cref="Changed"/> and
/// <see cref="Unread"/>, the in-app popup through <see cref="NewMessage"/>, and an
/// operating system notification for each newly arrived message.
///
/// A message counts as unread when it is addressed to this driver and not yet marked
/// read, or when it is a broadcast or route message created after the last time the
/// notifications tab was opened.
/// </remarks>
public class MessageWatch
{
    private readonly DriverDataService _data;
    private readonly ILocalNotifier _notifier;

    private const string SeenTsKey = "msg_seen_ts";

    /// <summary>How often messages are checked.</summary>
    /// <remarks>
    /// Runs for the whole session, in the background too, since it raises the system
    /// notification, so it is the app's most frequent request. A message waits at most this
    /// long for its badge and notification. The notifications tab reads on its own while it
    /// is open, and pull to refresh checks at once.
    /// </remarks>
    private const int PollMs = 15000;

    private System.Threading.Timer? _timer;
    private int _userId;
    private bool _seeded;
    private readonly HashSet<long> _known = new();
    private List<MessageModel> _msgs = new();
    private DateTime _seenTs = DateTime.MinValue; // cached; never block on SecureStorage

    public int Unread { get; private set; }
    public event Action? Changed;
    public event Action<MessageModel>? NewMessage;

    public MessageWatch(DriverDataService data, ILocalNotifier notifier)
    {
        _data = data;
        _notifier = notifier;

        // Android can freeze an app left in the background, timer and all, so messages are
        // checked the moment the driver comes back rather than on the next tick.
        App.Returned += () => { if (_timer is not null) _ = Poll(); };
    }

    public void Start(int userId)
    {
        if (_timer is not null && _userId == userId) return; // already running
        Stop();
        _userId = userId;
        _seeded = false;
        _known.Clear();
        _ = LoadSeenTs(); // async, non-blocking
        _timer = new System.Threading.Timer(_ => _ = Poll(), null, 0, PollMs);
    }

    private async Task LoadSeenTs()
    {
        try
        {
            var s = await SecureStorage.Default.GetAsync(SeenTsKey);
            if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, out var dt)) _seenTs = dt;
        }
        catch { /* first run */ }
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _msgs = new();
        Unread = 0;
    }

    /// <summary>Polls once on demand, such as a pull to refresh, so the badge updates
    /// without waiting for the timer.</summary>
    public Task RefreshNow() => Poll();

    private async Task Poll()
    {
        if (_userId == 0) return;
        try
        {
            var msgs = await _data.GetMessagesAsync(_userId);
            _msgs = msgs;

            bool firstPass = !_seeded;
            foreach (var m in msgs)
            {
                if (_known.Contains(m.MessageId)) continue;
                _known.Add(m.MessageId);

                // The first poll seeds what already exists, so no alert is raised for history.
                if (_seeded)
                {
                    NewMessage?.Invoke(m);
                    try { _notifier.Show((int)(m.MessageId & 0x7fffffff), m.Subject ?? "New message", m.Body ?? ""); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch.Notify] {ex}"); }
                }
            }
            _seeded = true;

            // On the first poll after launch, show only the newest unread direct message
            // that arrived while the app was closed, rather than one popup per message.
            if (firstPass)
            {
                var latestUnread = msgs.FirstOrDefault(m =>
                    (m.TargetAudience ?? "").ToLowerInvariant() == "driver" && !m.IsRead);
                if (latestUnread is not null) NewMessage?.Invoke(latestUnread);
            }

            Recompute();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch.Poll] {ex}"); }
    }

    /// <summary>Marks broadcast and route messages as seen, called when the notifications
    /// tab opens.</summary>
    /// <remarks>The cutoff is the later of now and the newest message, so a row dated in
    /// the future is still cleared.</remarks>
    public void MarkSeenNow()
    {
        var cutoff = PhTime.Now;
        foreach (var m in _msgs)
        {
            var t = PhTime.Raw(m.CreatedAt);
            if (t > cutoff) cutoff = t;
        }
        _seenTs = cutoff;
        _ = PersistSeenTs(_seenTs); // fire-and-forget, never block UI
        Recompute();
    }

    private static async Task PersistSeenTs(DateTime ts)
    {
        try { await SecureStorage.Default.SetAsync(SeenTsKey, ts.ToString("o")); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch.Persist] {ex}"); }
    }

    /// <summary>Refreshes the badge immediately after a message is marked read.</summary>
    public void Recompute()
    {
        int n = 0;
        foreach (var m in _msgs)
        {
            var aud = (m.TargetAudience ?? "").ToLowerInvariant();
            if (aud == "driver")
            {
                if (!m.IsRead) n++;
            }
            else // broadcast / route: no per-user read state -> use last-seen cutoff
            {
                if (PhTime.Raw(m.CreatedAt) > _seenTs) n++;
            }
        }
        Unread = n;
        Changed?.Invoke();
    }
}
