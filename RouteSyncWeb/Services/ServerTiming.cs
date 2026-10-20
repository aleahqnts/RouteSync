using System.Diagnostics;

namespace FleetWise.Services
{
    /// <summary>
    /// Counts the outgoing HTTP calls made while serving one request, and the time they took.
    /// </summary>
    /// <remarks>
    /// Every outgoing call is to Supabase, so this is the request's database cost. It is read
    /// from the activities System.Net.Http records for each call, which carry the caller's
    /// async context, so each call is credited to the request that made it. Calls that run
    /// side by side are summed, so the total can exceed the request's own time.
    /// </remarks>
    public static class ServerTiming
    {
        public sealed class Tally
        {
            public int Calls;
            public long Ticks;
        }

        private static readonly AsyncLocal<Tally?> Current = new();

        /// <summary>Starts listening. Call once at startup.</summary>
        public static void Enable()
        {
            // Recording the activities would otherwise add a traceparent header to every
            // database call, which nothing on the other side reads.
            DistributedContextPropagator.Current = DistributedContextPropagator.CreateNoOutputPropagator();
            ActivitySource.AddActivityListener(new ActivityListener
            {
                ShouldListenTo = source => source.Name == "System.Net.Http",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (Current.Value is not { } tally) return;
                    Interlocked.Increment(ref tally.Calls);
                    Interlocked.Add(ref tally.Ticks, activity.Duration.Ticks);
                },
            });
        }

        /// <summary>Starts a fresh count for the request now being served.</summary>
        public static Tally Begin() => Current.Value = new Tally();
    }
}
