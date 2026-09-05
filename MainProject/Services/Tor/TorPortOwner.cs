using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MyLovelyMail.MainProject.Services.Tor
{
    /// <summary>
    /// Which process is listening on a local port. This is what makes a "this port is Tor" verdict
    /// safe to remember: the verdict is not about an address, it is about the program answering
    /// there, and a port outlives the program that held it.
    /// <para>
    /// Measured rather than assumed, because the alternative was measured too: with a verdict kept
    /// on time alone, closing Tor Browser and putting a plain SOCKS5 proxy on the same port sent
    /// <c>imap.gmail.com</c> straight to that proxy — the impostor's own log showed the CONNECT
    /// request arrive — while the app's log named the Tor route. Asking the OS who owns the socket
    /// costs microseconds and no network at all.
    /// </para>
    /// </summary>
    public static class TorPortOwner
    {
        /// <summary>The process id listening on <paramref name="port"/> at <paramref name="host"/>, or null when it cannot be told.</summary>
        public static int? Of(string host, int port)
        {
            if (!OperatingSystem.IsWindows()) return null;
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return null;

            try
            {
                return OwnerOnWindows(address, port);
            }
            catch (Exception ex)
            {
                Log($"Could not read the owner of port {port}: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        const int AfInet = 2;
        const int TcpTableOwnerPidListener = 3;
        const uint NoError = 0;
        const uint ErrorInsufficientBuffer = 122;

        [StructLayout(LayoutKind.Sequential)]
        struct TcpRowOwnerPid
        {
            public uint State;
            public uint LocalAddress;
            public uint LocalPort;
            public uint RemoteAddress;
            public uint RemotePort;
            public uint OwningPid;
        }

        [SupportedOSPlatform("windows")]
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int addressFamily, int tableClass, int reserved);

        [SupportedOSPlatform("windows")]
        static int? OwnerOnWindows(IPAddress address, int port)
        {
            int size = 0;
            uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (status != ErrorInsufficientBuffer && status != NoError) return null;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != NoError) return null;

                int rows = Marshal.ReadInt32(buffer);
                IntPtr cursor = buffer + sizeof(int);
                int rowSize = Marshal.SizeOf<TcpRowOwnerPid>();

                for (int i = 0; i < rows; i++, cursor += rowSize)
                {
                    var row = Marshal.PtrToStructure<TcpRowOwnerPid>(cursor);
                    // The port arrives in network byte order in the low two bytes.
                    int rowPort = ((int)(row.LocalPort & 0xFF) << 8) | (int)((row.LocalPort >> 8) & 0xFF);
                    if (rowPort != port) continue;

                    // A listener on 0.0.0.0 serves the loopback address too, so it counts as the owner.
                    var rowAddress = new IPAddress(row.LocalAddress);
                    if (rowAddress.Equals(address) || rowAddress.Equals(IPAddress.Any))
                        return (int)row.OwningPid;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return null;
        }
    }
}
