using System.Text.Json;
using Newtonsoft.Json;

namespace FleetWise.Services
{
    /// <summary>
    /// Several reads answered in one request by a database function, each list under its own
    /// name, read into the same models a separate table read would give.
    /// </summary>
    /// <remarks>
    /// <para>Every request the API answers is logged and the log is metered, so a page that
    /// polls pays for each separate read it makes, however little each carries. A function
    /// that gathers a poll's reads into one JSON object answers them for the price of one.</para>
    ///
    /// <para>Rows are read with the same settings the API client uses for a table read, so a
    /// model comes out exactly as it would have from its own query, dates and all. Code built
    /// on those reads does not have to know they now arrive together.</para>
    /// </remarks>
    public sealed class ReadBundle
    {
        private static readonly JsonSerializerSettings RowSettings =
            Postgrest.Client.SerializerSettings(new Postgrest.ClientOptions());

        private readonly Dictionary<string, string?> _parts;

        private ReadBundle(Dictionary<string, string?> parts) => _parts = parts;

        /// <summary>Splits a function's answer into its named parts.</summary>
        public static ReadBundle Parse(string? json)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var parts = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                parts[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetRawText();
            return new ReadBundle(parts);
        }

        /// <summary>The rows under <paramref name="name"/>, or none when it is missing or null.</summary>
        public List<T> Rows<T>(string name) =>
            _parts.TryGetValue(name, out var raw) && raw is not null
                ? JsonConvert.DeserializeObject<List<T>>(raw, RowSettings) ?? new List<T>()
                : new List<T>();

        /// <summary>Whether <paramref name="name"/> was read: present and not null.</summary>
        public bool Has(string name) => _parts.TryGetValue(name, out var raw) && raw is not null;

        /// <summary>
        /// A section holding reads of its own, or null when the function could not read it.
        /// </summary>
        public ReadBundle? Section(string name) =>
            _parts.TryGetValue(name, out var raw) && raw is not null ? Parse(raw) : null;
    }
}
