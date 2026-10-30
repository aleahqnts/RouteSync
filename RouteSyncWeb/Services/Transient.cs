using System.Net.Sockets;

namespace FleetWise.Services
{
    // Retries a call that died before the server answered.
    //
    // A pooled connection closed at the far end is refused at the socket, so the request
    // never arrives and the pool replaces it. The second attempt therefore succeeds.
    //
    // Only wire failures qualify. Anything answered, timeouts included, passes through:
    // repeating an insert that already ran is worse than failing.
    public static class Transient
    {
        private const int Attempts = 3;
        private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(120);

        public static async Task<T> RunAsync<T>(Func<Task<T>> call)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await call();
                }
                catch (Exception ex) when (attempt < Attempts && IsBrokenConnection(ex))
                {
                    await Task.Delay(Pause * attempt);
                }
            }
        }

        public static async Task RunAsync(Func<Task> call)
            => await RunAsync<bool>(async () => { await call(); return true; });

        // Whether the request died on the wire rather than being answered.
        private static bool IsBrokenConnection(Exception ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
            {
                if (e is SocketException) return true;
                if (e is IOException) return true;
            }
            return false;
        }
    }
}
