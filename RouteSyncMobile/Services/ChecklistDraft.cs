using System.Text.Json;

namespace FleetWiseMobile.Services;

/// <summary>
/// Holds a walk-around in progress so it survives the app being put aside.
/// </summary>
/// <remarks>
/// Taking a photograph hands the screen to the system camera, which is among the largest
/// things a phone runs, and the operating system may reclaim this app behind it. Without
/// somewhere to write the answers down, a driver four sections into an inspection would
/// come back to an empty one.
///
/// The risk is not confined to the camera. A telephone call has always been able to do
/// this; the camera only makes it ordinary.
///
/// Only one inspection is held. A driver walks around one bus at a time, and a draft for
/// a different trip is of no use to the trip in front of them.
/// </remarks>
public class ChecklistDraft
{
    private const string Key = "checklist_draft";

    public record Saved(
        string TripId,
        int Section,
        Dictionary<int, string> Statuses,
        Dictionary<int, string> Photos);

    /// <summary>Writes the current state, replacing any earlier draft.</summary>
    public void Save(string tripId, int section,
        Dictionary<int, string> statuses, Dictionary<int, Guid> photos)
    {
        try
        {
            var saved = new Saved(tripId, section, statuses,
                photos.ToDictionary(p => p.Key, p => p.Value.ToString("N")));
            Preferences.Default.Set(Key, JsonSerializer.Serialize(saved));
        }
        catch (Exception ex)
        {
            // A draft that cannot be written is a lost walk-around at worst, which is what
            // happened before this existed. It is not worth failing the inspection over.
            System.Diagnostics.Debug.WriteLine($"[ChecklistDraft] save {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Reads back the draft for this trip, or null when there is none for it.
    /// </summary>
    public Saved? Load(string tripId)
    {
        try
        {
            var raw = Preferences.Default.Get<string?>(Key, null);
            if (string.IsNullOrEmpty(raw)) return null;

            var saved = JsonSerializer.Deserialize<Saved>(raw);
            return saved?.TripId == tripId ? saved : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChecklistDraft] load {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Forgets the draft, once the inspection has been submitted or skipped.
    /// </summary>
    /// <remarks>
    /// The photograph names go with it. They are only useful for matching an object
    /// already uploaded to the item it belongs to, and the submit has just done that.
    /// </remarks>
    public void Clear()
    {
        try { Preferences.Default.Remove(Key); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChecklistDraft] clear {ex.GetType().Name}");
        }
    }

    /// <summary>Reads the photograph names from a draft, as the identifiers they are.</summary>
    public static Dictionary<int, Guid> PhotosOf(Saved saved)
    {
        var photos = new Dictionary<int, Guid>();
        foreach (var (itemId, name) in saved.Photos)
            if (Guid.TryParse(name, out var id)) photos[itemId] = id;
        return photos;
    }
}
