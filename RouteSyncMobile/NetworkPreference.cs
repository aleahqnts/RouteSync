using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FleetWiseMobile;

/// <summary>
/// Keeps the app off IPv6 on the networks where IPv6 does not actually carry traffic.
/// </summary>
/// <remarks>
/// A router running DNS64 answers every name lookup with a synthetic address in
/// 64:ff9b::/96 alongside the real one. When the translator behind it is missing or
/// broken, that address accepts a connection attempt and then never answers. The runtime
/// tries the addresses it was handed in order and has no fallback race between them, and
/// a client's timeout covers all of the attempts together, so one dead address in front
/// consumes the entire budget and every call fails as though the device were offline.
/// Browsers hide this by racing the two families; nothing here does.
///
/// Turning IPv6 off inside the process avoids the dead address entirely. That is only
/// safe while the device has somewhere else to go, so it is done on the condition that
/// IPv4 is actually configured: a carrier that hands out IPv6 alone keeps it, because
/// there the same setting would take the app off the network completely.
///
/// This has to run before the first request, because the runtime reads the setting once
/// and keeps the answer. It reaches every client in the process, including the ones
/// inside the Supabase library that nothing here can configure, which is why the choice
/// is made here rather than on a handler.
/// </remarks>
public static class NetworkPreference
{
    public static void Apply()
    {
        var ipv4 = HasRoutableIPv4();
        if (ipv4) AppContext.SetSwitch("System.Net.DisableIPv6", true);

        // Recorded because the symptom of getting this wrong is every request timing
        // out, which looks the same as being offline and says nothing about the cause.
        System.Diagnostics.Debug.WriteLine(
            $"[NetworkPreference] routable IPv4: {ipv4}; IPv6 disabled: {ipv4}");
    }

    /// <summary>
    /// Whether any live interface holds an IPv4 address that can leave the device.
    /// </summary>
    /// <remarks>
    /// Loopback and the 169.254 range a device assigns itself when no lease arrives are
    /// both excluded: neither reaches anything, so neither is a reason to give up IPv6.
    /// </remarks>
    private static bool HasRoutableIPv4()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = addr.Address.GetAddressBytes();
                    if (b[0] == 127) continue;
                    if (b[0] == 169 && b[1] == 254) continue;
                    return true;
                }
            }
        }
        catch
        {
            // A device that will not describe its own interfaces is not evidence that
            // IPv6 is broken, so it keeps the runtime's own choice.
        }
        return false;
    }
}
