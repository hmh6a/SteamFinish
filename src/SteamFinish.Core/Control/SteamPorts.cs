using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SteamFinish.Core.Control;

/// <summary>
/// Finds which local ports the Steam client is listening on.
///
/// Steam's control channel defaults to 8080, but that is a popular port — Docker, WSL and half the
/// dev servers ever written want it too — and Steam simply fails to open the channel when it is
/// taken. Steam does accept <c>-devtools-port</c>, so the port cannot be assumed. Rather than making
/// the user tell the app which port to use, the app asks Windows which ports Steam actually holds
/// and tries those.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class SteamPorts
{
    private const int AF_INET = 2;

    /// <summary>TCP_TABLE_OWNER_PID_LISTENER: listening sockets, with the process that owns each.</summary>
    private const int TcpTableOwnerPidListener = 3;

    private const int NO_ERROR = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>
    /// Every loopback port a running <c>steam.exe</c> is listening on, most likely first. Empty when
    /// Steam is not running or the table cannot be read.
    /// </summary>
    public static IReadOnlyList<int> ListeningPorts()
    {
        var owners = SteamProcessIds();
        if (owners.Count == 0)
        {
            return [];
        }

        var ports = new List<int>();
        foreach (var (port, pid) in Listeners())
        {
            if (owners.Contains(pid) && !ports.Contains(port))
            {
                ports.Add(port);
            }
        }

        // Steam opens its ports in a stable order and the DevTools one is created late, but nothing
        // guarantees that, so the caller probes them all. Ascending just makes the log readable.
        ports.Sort();
        return ports;
    }

    /// <summary>A port nothing is listening on, for handing to Steam as <c>-devtools-port</c>.</summary>
    public static int FindFreePort()
    {
        // Binding to port 0 makes Windows pick one that is free, and closing it straight away leaves
        // it free. There is a race in principle; in practice Windows does not hand the same port out
        // again this quickly, and Steam failing to bind is reported rather than silently wrong.
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static HashSet<uint> SteamProcessIds()
    {
        var ids = new HashSet<uint>();
        try
        {
            foreach (var process in Process.GetProcessesByName("steam"))
            {
                ids.Add((uint)process.Id);
                process.Dispose();
            }
        }
        catch (Exception)
        {
            // An unreadable process list just means no candidates; 8080 is still tried.
        }

        return ids;
    }

    private static IEnumerable<(int Port, uint ProcessId)> Listeners()
    {
        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TcpTableOwnerPidListener, 0);
        if (status != ERROR_INSUFFICIENT_BUFFER || size <= 0)
        {
            yield break;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TcpTableOwnerPidListener, 0);
            if (status != NO_ERROR)
            {
                yield break;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRow>();

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(buffer + sizeof(int) + (i * rowSize));

                // The port arrives as two big-endian bytes inside a little-endian field.
                var port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                yield return (port, row.OwningProcessId);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>MIB_TCPROW_OWNER_PID. The remote pair is always zero for a listener, but it is part
    /// of the row and leaving it out would misread every row after the first.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetExtendedTcpTable(
        IntPtr table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool sorted,
        int addressFamily,
        int tableClass,
        int reserved);
}
