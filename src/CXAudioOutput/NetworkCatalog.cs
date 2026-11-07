using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CXAudioOutput;

public sealed record LanAddress(string Ip, string InterfaceName, int Score, string AdapterId = "")
{
    public string BaseUrl(int port) => $"http://{Ip}:{port}";
}

public sealed record NetworkAdapterSnapshot(
    string Id,
    string Name,
    string Description,
    string TypeLabel,
    bool IsUp,
    string StatusText,
    long SpeedBitsPerSecond,
    string MacAddress,
    IReadOnlyList<IPAddress> Ipv4Addresses,
    IReadOnlyList<IPAddress> Ipv6Addresses,
    bool IsVirtual,
    DateTimeOffset UpdatedAt)
{
    public bool HasUsableIpv4 => IsUp && Ipv4Addresses.Count > 0;

    public string Ipv4Text => Ipv4Addresses.Count == 0
        ? "未分配 IPv4"
        : string.Join("  ·  ", Ipv4Addresses.Select(address => address.ToString()));

    public string SpeedText
    {
        get
        {
            if (SpeedBitsPerSecond <= 0) return "速率未知";
            if (SpeedBitsPerSecond >= 1_000_000_000)
            {
                return (SpeedBitsPerSecond / 1_000_000_000d).ToString("0.#") + " Gbps";
            }

            if (SpeedBitsPerSecond >= 1_000_000)
            {
                return (SpeedBitsPerSecond / 1_000_000d).ToString("0.#") + " Mbps";
            }

            return (SpeedBitsPerSecond / 1_000d).ToString("0.#") + " Kbps";
        }
    }
}

public static class NetworkCatalog
{
    public static IReadOnlyList<NetworkAdapterSnapshot> Scan()
    {
        var result = new List<NetworkAdapterSnapshot>();
        var now = DateTimeOffset.Now;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                try
                {
                    var isUp = nic.OperationalStatus == OperationalStatus.Up;
                    var properties = nic.GetIPProperties();
                    var ipv4 = new List<IPAddress>();
                    var ipv6 = new List<IPAddress>();

                    foreach (var unicast in properties.UnicastAddresses)
                    {
                        var address = unicast.Address;
                        if (IPAddress.IsLoopback(address)) continue;
                        if (address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var text = address.ToString();
                            if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                            if (text.StartsWith("0.", StringComparison.Ordinal)) continue;
                            ipv4.Add(address);
                        }
                        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
                            {
                                continue;
                            }

                            ipv6.Add(address);
                        }
                    }

                    long speed = 0;
                    try
                    {
                        speed = nic.Speed;
                    }
                    catch
                    {
                        // Some virtual adapters throw when speed is queried.
                    }

                    var name = string.IsNullOrWhiteSpace(nic.Name) ? "网络适配器" : nic.Name;
                    var description = string.IsNullOrWhiteSpace(nic.Description) ? name : nic.Description;
                    var hint = (name + " " + description).ToLowerInvariant();
                    var isVirtual = IsVirtualInterface(hint)
                                    || nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp;

                    result.Add(new NetworkAdapterSnapshot(
                        nic.Id,
                        name,
                        description,
                        TypeLabel(nic.NetworkInterfaceType),
                        isUp,
                        StatusLabel(nic.OperationalStatus),
                        speed,
                        FormatMac(nic.GetPhysicalAddress()),
                        ipv4.Distinct().ToList(),
                        ipv6.Distinct().ToList(),
                        isVirtual,
                        now));
                }
                catch
                {
                    // A single broken adapter must not hide the rest.
                }
            }
        }
        catch
        {
            // The UI will show an empty list and the user can refresh.
        }

        return result
            .OrderByDescending(adapter => adapter.IsUp)
            .ThenBy(adapter => adapter.IsVirtual)
            .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool IsVirtualInterface(string hint)
    {
        return hint.Contains("vethernet")
               || hint.Contains("vmware")
               || hint.Contains("virtual")
               || hint.Contains("hyper-v")
               || hint.Contains("wsl")
               || hint.Contains("vpn")
               || hint.Contains("tailscale")
               || hint.Contains("zerotier")
               || hint.Contains("wireguard")
               || hint.Contains("nordlynx")
               || hint.Contains("bluetooth")
               || hint.Contains("meta")
               || hint.Contains("tap-")
               || hint.Contains("tun");
    }

    private static string TypeLabel(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet => "以太网",
        NetworkInterfaceType.GigabitEthernet => "以太网",
        NetworkInterfaceType.FastEthernetT => "以太网",
        NetworkInterfaceType.FastEthernetFx => "以太网",
        NetworkInterfaceType.Ppp => "拨号 / VPN",
        NetworkInterfaceType.Tunnel => "隧道 / VPN",
        _ => type.ToString()
    };

    private static string StatusLabel(OperationalStatus status) => status switch
    {
        OperationalStatus.Up => "已连接",
        OperationalStatus.Down => "未连接",
        OperationalStatus.Dormant => "休眠",
        OperationalStatus.NotPresent => "不存在",
        OperationalStatus.LowerLayerDown => "链路断开",
        OperationalStatus.Testing => "检测中",
        OperationalStatus.Unknown => "未知",
        _ => status.ToString()
    };

    private static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 0) return "无 MAC";
        return string.Join(":", bytes.Select(value => value.ToString("X2")));
    }
}
