using FleetWise.Models;

namespace FleetWise.Services
{
    public class FareCalculator
    {
        private readonly decimal _fallbackRate;

        public FareCalculator(IConfiguration config)
        {
            _fallbackRate = config.GetValue<decimal?>("FleetWise:FareRate") ?? 15.00m;
        }

        /// <summary>
        /// The fleet's standard fare, from the fare configuration rows read from the database.
        /// </summary>
        /// <remarks>
        /// Falls back to the application settings value, and then to a default, if that
        /// table is empty, so revenue figures never fail outright.
        ///
        /// Callers read the rate once per request and reuse it for every bus.
        /// </remarks>
        public decimal RateFrom(IEnumerable<FareConfig> rows) =>
            rows.FirstOrDefault()?.StandardFare is decimal fare && fare > 0 ? fare : _fallbackRate;

        public decimal Estimate(int passengers, decimal rate) => passengers * rate;
    }
}
