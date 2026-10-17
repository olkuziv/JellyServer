using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using JellyServer.Configuration;
using Microsoft.Extensions.Options;

namespace JellyServer.Services;

/// <summary>
/// Answers Jellyfin's UDP auto-discovery ("who is JellyfinServer?") on port 7359.
/// Only reachable from the LAN with host networking or a published UDP port.
/// </summary>
public sealed class DiscoveryService(IOptions<ServerOptions> options, ServerIdentity identity, ILogger<DiscoveryService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opt = options.Value;
        if (!opt.Discovery) return;
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, 7359)) { EnableBroadcast = true };
        }
        catch (SocketException ex)
        {
            log.LogWarning("UDP discovery disabled: {Error}", ex.Message);
            return;
        }
        using (udp)
        {
            log.LogInformation("UDP discovery listening on :7359");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var msg = await udp.ReceiveAsync(ct);
                    if (!Encoding.UTF8.GetString(msg.Buffer).Contains("who is JellyfinServer?", StringComparison.OrdinalIgnoreCase)) continue;
                    var address = opt.PublicUrl ?? $"http://{LocalAddressFor(msg.RemoteEndPoint.Address)}:{opt.Port}";
                    var reply = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string?>
                    {
                        ["Address"] = address, ["Id"] = identity.ServerId, ["Name"] = opt.Name, ["EndpointAddress"] = null,
                    });
                    await udp.SendAsync(reply, msg.RemoteEndPoint, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex) { log.LogDebug("Discovery error: {Error}", ex.Message); }
            }
        }
    }

    private static string LocalAddressFor(IPAddress remote)
    {
        using var probe = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(remote, 7359);
        return ((IPEndPoint)probe.LocalEndPoint!).Address.ToString();
    }
}
