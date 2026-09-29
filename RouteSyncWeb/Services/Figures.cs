using System.Globalization;

namespace FleetWise.Services
{
    /// <summary>Counts written short enough to sit in a card whatever they grow to.</summary>
    public static class Figures
    {
        /// <summary>
        /// A count in at most four characters and a suffix: 950, 1.6k, 16k, 160k, 1.2M.
        /// </summary>
        /// <remarks>
        /// One decimal only while it still says something, below ten of the unit, and never a
        /// trailing ".0". Rounded down, so a card never claims a figure not yet reached.
        /// </remarks>
        public static string Compact(long n)
        {
            if (n < 0) return "-" + Compact(-n);
            if (n < 1_000) return n.ToString(CultureInfo.InvariantCulture);

            var (value, unit) = n < 1_000_000 ? (n / 1_000d, "k") : (n / 1_000_000d, "M");
            var shown = value < 10
                ? Math.Floor(value * 10) / 10
                : Math.Floor(value);
            return shown.ToString("0.#", CultureInfo.InvariantCulture) + unit;
        }
    }
}
