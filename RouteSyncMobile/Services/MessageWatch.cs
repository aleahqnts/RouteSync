using FleetWiseMobile.Models;
using Microsoft.Maui.Storage;

namespace FleetWiseMobile.Services;

// Background poller for driver messages, running on its own timer for the whole
// signed-in session regardless of which page is shown.
//
// Drives three things: the unread badge through Changed and
// Unread, the in-app popup through NewMessage, and an
// operating system notification for each newly arrived message.
//
// Every message can be read or unread. A message addressed to this driver carries its
// read mark in the database. A broadcast or route message goes to many drivers and has
// no mark of its own there, so this phone keeps one for it.
public class MessageWatch
{
    private readonly DriverDataService _data;
    private readonly ILocalNotifier _notifier;

    // How often messages are checked.
    //
    // Runs for the whole session, in the background too, since it raises the system
    // notification, so it is the app's most frequent request. A message waits at most this
    // long for its badge and notification. The notifications tab reads on its own while it
    // is open, and pull to refresh checks at once.
    private const int PollMs = 15000;

    // The most broadcast and route read marks kept, newest messages first.
    private const int ReadMarksKept = 500;

    private System.Threading.Timer? _timer;
    private int _userId;
    private bool _seeded;
    // The newest message id seen. Only messages above it are new arrivals: older history
    // paged in by "Show older messages" lands below it and raises no alert.
    private long _newest;
    private List<MessageModel> _msgs = new();

    // Broadcast and route messages this phone has read, and the moment before which all
    // of them count as read: the first time this driver signed in here, so a new phone
    // does not open on a backlog of other drivers' history.
    private readonly HashSet<long> _readMarks = new();
    private DateTime _readSince = DateTime.MinValue;

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
        _newest = 0;
        LoadReadMarks();
        _timer = new System.Threading.Timer(_ => _ = Poll(), null, 0, PollMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _msgs = new();
        Unread = 0;
    }

    // Polls once on demand, such as a pull to refresh, so the badge updates
    // without waiting for the timer.
    public Task RefreshNow() => Poll();

    private async Task Poll()
    {
        if (_userId == 0) return;
        try
        {
            var msgs = await _data.GetMessagesAsync(_userId);
            _msgs = msgs;

            bool firstPass = !_seeded;
            var newest = _newest;
            // Oldest first, so several arrivals queue in the order they were sent.
            foreach (var m in msgs.Where(m => m.MessageId > newest).OrderBy(m => m.MessageId))
            {
                // The first poll seeds what already exists, so no alert is raised for history.
                if (_seeded)
                {
                    NewMessage?.Invoke(m);
                    try { _notifier.Show((int)(m.MessageId & 0x7fffffff), m.Subject ?? "New message", m.Body ?? ""); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch.Notify] {ex}"); }
                }
                _newest = Math.Max(_newest, m.MessageId);
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

    // Whether the driver has yet to read m.
    public bool IsUnread(MessageModel m)
    {
        if ((m.TargetAudience ?? "").ToLowerInvariant() == "driver") return !m.IsRead;
        return PhTime.Raw(m.CreatedAt) > _readSince && !_readMarks.Contains(m.MessageId);
    }

    // Marks a message read, in the database for a direct one and here for the rest.
    public Task MarkReadAsync(MessageModel m) => MarkReadAsync(new[] { m });

    // Marks every one of messages read.
    //
    // The local marks are taken first, so the badge drops at once. A direct message whose
    // mark cannot be sent stays unread, and the badge says so after the next check.
    public async Task MarkReadAsync(IEnumerable<MessageModel> messages)
    {
        var unread = messages.Where(IsUnread).ToList();
        if (unread.Count == 0) return;

        var direct = new List<MessageModel>();
        foreach (var m in unread)
        {
            if ((m.TargetAudience ?? "").ToLowerInvariant() == "driver") direct.Add(m);
            else _readMarks.Add(m.MessageId);
        }
        SaveReadMarks();

        foreach (var m in direct)
        {
            try
            {
                await _data.MarkMessageReadAsync(m.MessageId);
                m.IsRead = true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch.Read] {ex}"); }
        }
        Recompute();
    }

    // Refreshes the badge immediately after a message is marked read.
    public void Recompute()
    {
        Unread = _msgs.Count(IsUnread);
        Changed?.Invoke();
    }

    private string MarksKey => $"msg_read_marks_{_userId}";
    private string SinceKey => $"msg_read_since_{_userId}";

    // Preferences rather than secure storage: read marks are not secret, and these are read
    // synchronously so the first badge is already right.
    private void LoadReadMarks()
    {
        _readMarks.Clear();
        try
        {
            var since = Preferences.Default.Get(SinceKey, 0L);
            if (since == 0)
            {
                _readSince = PhTime.Now;
                Preferences.Default.Set(SinceKey, _readSince.Ticks);
            }
            else _readSince = new DateTime(since);

            foreach (var part in Preferences.Default.Get(MarksKey, "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(part, out var id)) _readMarks.Add(id);
        }
        catch (Exception ex)
        {
            _readSince = PhTime.Now;
            System.Diagnostics.Debug.WriteLine($"[Watch.LoadMarks] {ex}");
        }
    }

    private void SaveReadMarks()
    {
        try
        {
            var kept = _readMarks.OrderByDescending(id => id).Take(ReadMarksKept).ToList();
            Preferences.Default.Set(MarksKey, string.Join(',', kept));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch.SaveMarks] {ex}"); }
    }
}
