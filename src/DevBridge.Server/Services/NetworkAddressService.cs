using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DevBridge.Options;
using Microsoft.Extensions.Options;

namespace DevBridge.Services;

public sealed class NetworkAddressService(IOptions<DevBridgeOptions> options)
{
    public string Resolve(HttpRequest request)
    {
        var configured = options.Value.PublicBaseUrl.TrimEnd('/');
        if (configured.Length > 0) return configured;
        var port = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(x => x.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString() ?? request.Host.Host;
        return $"{request.Scheme}://{address}{(port is 80 or 443 ? "" : $":{port}")}";
    }
}
