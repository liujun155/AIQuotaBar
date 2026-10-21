using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace AIQuotaBar.Core.Credentials;

// Keep the CSRF token in memory only; do not include it in logs or ToString().
public sealed class AntigravityLocalEndpoint
{
    public AntigravityLocalEndpoint(int port, string csrfToken)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Port = port;
        CsrfToken = csrfToken;
    }
    public int Port { get; }
    public string CsrfToken { get; }
    public override string ToString() => $"Antigravity localhost:{Port}";
}

public sealed class AntigravityLocalServerDiscovery
{
    private readonly object _gate = new();
    private IReadOnlyList<AntigravityLocalEndpoint> _cached = [];
    private DateTimeOffset _expiresAt;

    public IReadOnlyList<AntigravityLocalEndpoint> Discover(bool force = false)
    {
        if (!OperatingSystem.IsWindows()) return [];
        lock (_gate)
        {
            if (!force && DateTimeOffset.UtcNow < _expiresAt) return _cached;
            try { _cached = DiscoverWindows(); }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
            {
                _cached = [];
            }
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(_cached.Count > 0 ? 60 : 10);
            return _cached;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<AntigravityLocalEndpoint> DiscoverWindows()
    {
        var result = new List<AntigravityLocalEndpoint>();
        var listeners = ReadListeners();
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name LIKE 'language_server%'");
        searcher.Options.Timeout = TimeSpan.FromSeconds(3);
        using var processes = searcher.Get();
        foreach (ManagementObject process in processes)
        {
            using (process)
            {
                var path = process["ExecutablePath"] as string;
                if (path?.Contains("antigravity", StringComparison.OrdinalIgnoreCase) != true) continue;
                var commandLine = process["CommandLine"] as string ?? "";
                var match = Regex.Match(commandLine, "--csrf_token(?:=|\\s+)(?:\"([^\"]+)\"|([^\\s\"]+))");
                if (!match.Success) continue;
                var token = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                var pid = Convert.ToUInt32(process["ProcessId"]);
                foreach (var port in listeners.Where(l => l.Pid == pid).Select(l => l.Port).Distinct())
                    result.Add(new AntigravityLocalEndpoint(port, token));
            }
        }
        return result;
    }

    // Read listening ports by PID without launching PowerShell or inspecting unrelated app data.
    [SupportedOSPlatform("windows")]
    private static List<(uint Pid, int Port)> ReadListeners()
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (size < 4) return [];
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0) != 0) return [];
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRow>();
            var result = new List<(uint, int)>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(IntPtr.Add(buffer, 4 + i * rowSize));
                var port = (ushort)IPAddress.NetworkToHostOrder(unchecked((short)row.LocalPort));
                if (port > 0) result.Add((row.Pid, port));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid;
    }
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
