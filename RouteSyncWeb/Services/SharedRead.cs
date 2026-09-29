using Microsoft.Extensions.Caching.Memory;

namespace FleetWise.Services
{
    /// <summary>
    /// A read shared by everyone who asks for the same thing within a short time.
    /// </summary>
    /// <remarks>
    /// <para>For figures many people watch at once and the database would otherwise be asked
    /// for once per viewer. The first caller reads; everyone asking for the same key until
    /// the answer expires is handed that answer, including callers who arrive while the read
    /// is still under way, so a room of viewers costs the database one read.</para>
    ///
    /// <para>A read that fails is not kept, so the next caller tries again rather than being
    /// handed the same failure until it expires.</para>
    /// </remarks>
    public static class SharedRead
    {
        private static readonly object Gate = new();

        /// <summary>The answer held under <paramref name="key"/>, read afresh once it has expired.</summary>
        /// <param name="cache">Where answers are held between callers.</param>
        /// <param name="key">What is being read, including anything that narrows it.</param>
        /// <param name="lifetime">How long an answer is handed out after it was read.</param>
        /// <param name="read">Reads the answer.</param>
        /// <param name="fresh">Read now even if an answer is held, and hold the new one instead.</param>
        public static Task<T> GetAsync<T>(IMemoryCache cache, string key, TimeSpan lifetime,
            Func<Task<T>> read, bool fresh = false)
        {
            Lazy<Task<T>> shared;
            var created = false;
            lock (Gate)
            {
                if (fresh || !cache.TryGetValue(key, out Lazy<Task<T>>? held) || held is null)
                {
                    held = new Lazy<Task<T>>(() => Started(read));
                    cache.Set(key, held, lifetime);
                    created = true;
                }
                shared = held;
            }

            var task = shared.Value;
            if (!created) return task;

            task.ContinueWith(_ =>
            {
                lock (Gate)
                {
                    if (cache.TryGetValue(key, out Lazy<Task<T>>? current) && ReferenceEquals(current, shared))
                        cache.Remove(key);
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }

        // A read that throws before it first waits still ends as a failed task, which is
        // dropped like any other failure rather than held as a thrown exception.
        private static async Task<T> Started<T>(Func<Task<T>> read) => await read();
    }
}
