using System.Net;
using System.Reflection;

namespace FleetWiseMobile.Services;

/// <summary>Has the database client ask for compressed answers.</summary>
/// <remarks>
/// The postgrest client sends every request through one HttpClient of its own, which never
/// asks for compression, so every answer crossed the network at full size. The data leaving
/// the database is what the hosting plan meters. Once its handler accepts gzip and deflate,
/// it asks for them and unpacks the answer before the client reads it, and JSON travels four
/// to eight times smaller.
///
/// The client offers no way to pass in a handler, so its handler is reached by reflection.
/// That has to happen before the client's first request, after which a handler cannot be
/// changed. If either library changes shape, nothing is changed and requests go on
/// uncompressed.
/// </remarks>
public static class CompressedReads
{
    /// <summary>Turns compression on, and says whether it could.</summary>
    public static bool Enable()
    {
        try
        {
            var client = typeof(Postgrest.ClientOptions).Assembly.GetType("Postgrest.Helpers")
                ?.GetField("Client", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(null) as HttpClient;
            var handler = typeof(HttpMessageInvoker)
                .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(client) as HttpClientHandler;
            if (handler is null) return false;

            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
