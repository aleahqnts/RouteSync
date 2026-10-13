using System.Net;
using System.Reflection;
using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class CompressedReadsTests
{
    [Fact]
    public void The_database_client_asks_for_compressed_answers()
    {
        // Fails when an upgrade of postgrest-csharp or the runtime moves the client or its
        // handler, which would otherwise quietly send every answer uncompressed again.
        Assert.True(CompressedReads.Enable());

        var client = (HttpClient)typeof(Postgrest.ClientOptions).Assembly.GetType("Postgrest.Helpers")!
            .GetField("Client", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(null)!;
        var handler = (HttpClientHandler)typeof(HttpMessageInvoker)
            .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;

        Assert.True(handler.AutomaticDecompression.HasFlag(DecompressionMethods.GZip));
    }
}
