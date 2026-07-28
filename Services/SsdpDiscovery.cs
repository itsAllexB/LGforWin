using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace LGforWin.Services;

/// <summary>A TV found on the LAN via SSDP, ready to be added by one click.</summary>
public sealed record DiscoveredTv(string Name, string Host, string Model);

/// <summary>
/// Finds LG webOS TVs on the local network via SSDP (UPnP discovery). Sends an
/// M-SEARCH for the LG-specific "webos-second-screen" service — only webOS TVs
/// answer it — then fetches each responder's device-description XML for its
/// friendly name and model.
/// </summary>
public static class SsdpDiscovery
{
    private const string MulticastAddress = "239.255.255.250";
    private const int MulticastPort = 1900;
    private const string SearchTarget = "urn:lge-com:service:webos-second-screen:1";

    private static readonly string MSearch =
        "M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {MulticastAddress}:{MulticastPort}\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 2\r\n" +
        $"ST: {SearchTarget}\r\n" +
        "\r\n";

    /// <summary>
    /// Scans for TVs for roughly <paramref name="timeout"/> (default 4 s), reporting each unique
    /// TV through <paramref name="found"/> as it answers, and returns the complete list.
    /// Never throws; an interface that can't be bound is simply skipped.
    /// </summary>
    public static async Task<List<DiscoveredTv>> FindTvsAsync(
        IProgress<DiscoveredTv>? found = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var results = new List<DiscoveredTv>();
        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        var sockets = CreateSockets();
        if (sockets.Count == 0) return results;

        using var scan = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scan.CancelAfter(timeout ?? TimeSpan.FromSeconds(4));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        try
        {
            var listeners = sockets
                .Select(s => ListenAsync(s, http, gate, seenLocations, seenHosts, results, found, scan.Token))
                .ToList();

            // Fire the search a few times per socket — SSDP is UDP, replies can be lost.
            var searchBytes = Encoding.ASCII.GetBytes(MSearch);
            var target = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);
            for (var burst = 0; burst < 3; burst++)
            {
                foreach (var socket in sockets)
                {
                    try { await socket.SendAsync(searchBytes, target, scan.Token).ConfigureAwait(false); }
                    catch { /* interface may have gone away mid-scan */ }
                }
                try { await Task.Delay(150, scan.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            await Task.WhenAll(listeners).ConfigureAwait(false);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }

        ct.ThrowIfCancellationRequested();
        return results;
    }

    // Receives responses on one socket until the scan window closes. Never throws.
    private static async Task ListenAsync(
        UdpClient socket, HttpClient http, object gate,
        HashSet<string> seenLocations, HashSet<string> seenHosts,
        List<DiscoveredTv> results, IProgress<DiscoveredTv>? found, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await socket.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch { return; /* socket error — stop listening on this one */ }

            var location = ParseLocation(Encoding.UTF8.GetString(received.Buffer));
            if (location is null) continue;
            lock (gate)
            {
                if (!seenLocations.Add(location)) continue;
            }

            var tv = await DescribeAsync(http, location, ct).ConfigureAwait(false);
            if (tv is null) continue;
            lock (gate)
            {
                if (!seenHosts.Add(tv.Host)) continue;
                results.Add(tv);
            }
            found?.Report(tv);
        }
    }

    // One socket per IPv4 interface address (so multi-NIC machines search every subnet),
    // plus a wildcard socket as a catch-all.
    private static List<UdpClient> CreateSockets()
    {
        var sockets = new List<UdpClient>();

        void TryAdd(IPAddress bindAddress)
        {
            try
            {
                var client = new UdpClient(new IPEndPoint(bindAddress, 0));
                client.Client.ReceiveBufferSize = 64 * 1024;
                sockets.Add(client);
            }
            catch { /* address not bindable right now */ }
        }

        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address)
                .Distinct();
            foreach (var address in addresses) TryAdd(address);
        }
        catch { }

        TryAdd(IPAddress.Any);
        return sockets;
    }

    private static string? ParseLocation(string response)
    {
        foreach (var line in response.Split("\r\n"))
        {
            if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["LOCATION:".Length..].Trim();
                return Uri.IsWellFormedUriString(value, UriKind.Absolute) ? value : null;
            }
        }
        return null;
    }

    // Fetches the UPnP device description and turns it into a DiscoveredTv.
    private static async Task<DiscoveredTv?> DescribeAsync(HttpClient http, string location, CancellationToken ct)
    {
        var host = new Uri(location).Host;
        string name = "", model = "";
        try
        {
            var xml = await http.GetStringAsync(location, ct).ConfigureAwait(false);
            var doc = XDocument.Parse(xml);
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";
            var device = doc.Descendants(ns + "device").FirstOrDefault();
            name = device?.Element(ns + "friendlyName")?.Value?.Trim() ?? "";
            model = device?.Element(ns + "modelName")?.Value?.Trim() ?? "";
        }
        catch { /* description unreachable — still offer the TV by IP */ }

        // friendlyName is usually "[LG] webOS TV OLED55C34LA" — trim the boilerplate.
        name = name.Replace("[LG]", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("webOS TV", "", StringComparison.OrdinalIgnoreCase)
                   .Trim();
        if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(model) ? "LG TV" : model;

        return new DiscoveredTv(name, host, model);
    }
}
