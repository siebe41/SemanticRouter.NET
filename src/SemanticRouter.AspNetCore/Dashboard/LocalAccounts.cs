using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>Which account on this machine opened a loopback connection, as the operating system tells it.</summary>
internal enum LocalAccount
{
    /// <summary>
    /// The lookup works here, but did not name the client's account: the table lists the router's own end of the
    /// connection and not the client's (the client closed it, or it is a closed socket no process holds any more), or the
    /// client's owner could not be read. Not trusted: an error must not make another account's request this machine's.
    /// </summary>
    Unknown,

    /// <summary>The account the router runs as, or the machine's administrator (root, LocalSystem), who can read the router's keys anyway.</summary>
    Same,

    /// <summary>Another account on this machine.</summary>
    Other,

    /// <summary>
    /// The lookup is not available here: a platform without one, a connection without both addresses, or a table that
    /// cannot be read or does not list even the router's own end of the connection (WSL 1, some sandboxes). Trusted as
    /// this machine, as before the check existed, or the router could never be used without a token there.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Finds the account behind a loopback TCP connection, so that another user of a shared machine is not trusted as
/// "this machine": every program can connect to 127.0.0.1, whoever runs it. Linux reads <c>/proc/net/tcp</c> and
/// <c>tcp6</c>, macOS the kernel's list of TCP sockets (<c>net.inet.tcp.pcblist_n</c>, what netstat reads), Windows the
/// TCP table and the token of the process that owns the client's end. The answer is kept for the connection's lifetime.
/// </summary>
internal static class LocalAccounts
{
    private static readonly object CacheKey = new();

    /// <summary>The account behind this request's connection; <see cref="LocalAccount.Unavailable"/> without both addresses.</summary>
    public static LocalAccount Of(HttpContext context)
    {
        ConnectionInfo connection = context.Connection;
        if (connection.RemoteIpAddress is not { } remote || connection.LocalIpAddress is not { } local || connection.RemotePort == 0)
        {
            return LocalAccount.Unavailable;
        }

        // HTTP/2 serves requests of one connection at the same time, so its items are locked.
        IDictionary<object, object?>? items = context.Features.Get<IConnectionItemsFeature>()?.Items;
        if (items is not null)
        {
            lock (items)
            {
                if (items.TryGetValue(CacheKey, out object? known) && known is LocalAccount account)
                {
                    return account;
                }
            }
        }

        LocalAccount found = Find(new IPEndPoint(remote, connection.RemotePort), new IPEndPoint(local, connection.LocalPort));
        Remember(context, found);
        return found;
    }

    /// <summary>Keeps the answer for the rest of the connection (and lets tests say who is calling).</summary>
    internal static void Remember(HttpContext context, LocalAccount account)
    {
        if (context.Features.Get<IConnectionItemsFeature>()?.Items is { } items)
        {
            lock (items)
            {
                items[CacheKey] = account;
            }
        }
    }

    /// <summary>The account that owns <paramref name="client"/>'s end of its connection to <paramref name="server"/>, a socket of this process.</summary>
    public static LocalAccount Find(IPEndPoint client, IPEndPoint server)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(server);
        client = Normalize(client);
        server = Normalize(server);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return Compare(LinuxOwners(["/proc/net/tcp", "/proc/net/tcp6"], client, server));
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacTable(client, server) is { } owners ? Compare(owners) : LocalAccount.Unavailable;
            }

            if (OperatingSystem.IsWindows())
            {
                return Windows(client, server);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException
            or Win32Exception or SecurityException or ArgumentException or OutOfMemoryException)
        {
            // The table could not be read (a /proc/net the router may not read, a missing library): as on a platform
            // without one. A failure after the router's own end was found is Unknown instead (Compare, Windows).
        }

        return LocalAccount.Unavailable;
    }

    /// <summary>
    /// Same when both ends belong to one user (or the client is root); Unavailable when the router's own end was not
    /// found (the table is not usable here); Unknown when only the client's end was not.
    /// </summary>
    internal static LocalAccount Compare((uint? Client, uint? Server) owners) =>
        owners.Server is not { } server ? LocalAccount.Unavailable
        : owners.Client is not { } client ? LocalAccount.Unknown
        : client == server || client == 0 ? LocalAccount.Same
        : LocalAccount.Other;

    // Linux: each line of /proc/net/tcp is "sl local_address rem_address st tx:rx tr:when retrnsmt uid timeout inode ...".
    internal static (uint? Client, uint? Server) LinuxOwners(IEnumerable<string> tables, IPEndPoint client, IPEndPoint server)
    {
        uint? clientUid = null, serverUid = null;
        foreach (string table in tables.Where(File.Exists))
        {
            foreach (string line in File.ReadLines(table))
            {
                if (LinuxRow(line) is not { } row)
                {
                    continue;
                }

                if (row.Local.Equals(client) && row.Remote.Equals(server))
                {
                    clientUid = row.Uid;
                }
                else if (row.Local.Equals(server) && row.Remote.Equals(client))
                {
                    serverUid = row.Uid;
                }
            }
        }

        return (clientUid, serverUid);
    }

    // A row whose inode is 0 is a socket no process holds any more (closed, or a TIME_WAIT entry): the kernel prints uid 0
    // for TIME_WAIT, which would read as root, so such a row names no owner. A client that closed its end right after
    // sending a request is then not found (Unknown), rather than taken for root.
    internal static (IPEndPoint Local, IPEndPoint Remote, uint Uid)? LinuxRow(string line)
    {
        string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 10 && fields[0].EndsWith(':')
            && LinuxEndPoint(fields[1]) is { } local && LinuxEndPoint(fields[2]) is { } remote
            && uint.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out uint uid)
            && ulong.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out ulong inode) && inode != 0
            ? (local, remote, uid)
            : null;
    }

    private static IPEndPoint? LinuxEndPoint(string field)
    {
        int colon = field.IndexOf(':', StringComparison.Ordinal);
        if (colon is not (8 or 32)
            || !ushort.TryParse(field.AsSpan(colon + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort port))
        {
            return null;
        }

        // The kernel prints the address as 32-bit words, each in the machine's own byte order.
        Span<byte> bytes = stackalloc byte[colon / 2];
        for (int word = 0; word < colon / 8; word++)
        {
            if (!uint.TryParse(field.AsSpan(word * 8, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
            {
                return null;
            }

            if (BitConverter.IsLittleEndian)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[(word * 4)..], value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes[(word * 4)..], value);
            }
        }

        return Normalize(new IPEndPoint(new IPAddress(bytes), port));
    }

    // macOS: net.inet.tcp.pcblist_n is a struct xinpgen, then for each socket a group of records (xinpcb_n, xsocket_n, the
    // two buffers, the statistics, xtcpcb_n), each starting with its length and kind and padded to 8 bytes.
    private const uint XsoSocket = 0x01, XsoInpcb = 0x10, XsoAllTcp = 0x3F;
    private const int XinpgenSize = 24;
    private static readonly byte[] PcbListName = "net.inet.tcp.pcblist_n\0"u8.ToArray();

    [SupportedOSPlatform("macos")]
    private static (uint? Client, uint? Server)? MacTable(IPEndPoint client, IPEndPoint server) =>
        MacPcbList() is { } list ? MacOwners(list, client, server, GetEffectiveUserId(), Environment.ProcessId) : null;

    /// <summary>The kernel's list of TCP sockets, as netstat reads it; <see langword="null"/> when it cannot be read.</summary>
    [SupportedOSPlatform("macos")]
    internal static byte[]? MacPcbList()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            nuint length = 0;
            if (SysctlByName(PcbListName, null, ref length, IntPtr.Zero, 0) != 0 || length == 0)
            {
                return null;
            }

            // Room for connections opened between the two calls.
            byte[] list = new byte[(int)length + (int)length / 4 + 4096];
            length = (nuint)list.Length;
            if (SysctlByName(PcbListName, list, ref length, IntPtr.Zero, 0) == 0)
            {
                return list[..(int)length];
            }

            if (Marshal.GetLastPInvokeError() != 12) // ENOMEM: it grew meanwhile
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The owners of both ends, read from the socket records of the list. The private structures are laid out either
    /// naturally or packed to 4 bytes, which moves their fields, so the router's own end (<paramref name="self"/> and
    /// <paramref name="pid"/>) shows which layout this kernel uses before the client's end is read with it.
    /// </summary>
    internal static (uint? Client, uint? Server) MacOwners(ReadOnlySpan<byte> list, IPEndPoint client, IPEndPoint server, uint self, int pid)
    {
        if (list.Length < XinpgenSize)
        {
            return (null, null);
        }

        (int Start, int Length)? clientSocket = null, serverSocket = null;
        (int Start, int Length)? inpcb = null, socket = null;
        uint seen = 0;
        int offset = RoundUp(BitConverter.ToUInt32(list));
        while (offset + 8 <= list.Length)
        {
            uint length = BitConverter.ToUInt32(list[offset..]);
            uint kind = BitConverter.ToUInt32(list[(offset + 4)..]);
            if (length <= XinpgenSize || offset + length > list.Length)
            {
                break; // the closing xinpgen
            }

            if (kind == XsoSocket)
            {
                socket = (offset, (int)length);
            }
            else if (kind == XsoInpcb)
            {
                inpcb = (offset, (int)length);
            }

            seen |= kind;
            if ((seen & XsoAllTcp) == XsoAllTcp)
            {
                if (inpcb is { } i && socket is { } so)
                {
                    ReadOnlySpan<byte> record = list.Slice(i.Start, i.Length);
                    if (MacConnects(record, client, server))
                    {
                        clientSocket = so;
                    }
                    else if (MacConnects(record, server, client))
                    {
                        serverSocket = so;
                    }
                }

                (seen, inpcb, socket) = (0, null, null);
            }

            offset += RoundUp(length);
        }

        // Without the router's own end (or its layout) the list is not usable here; without the client's, it is gone.
        if (serverSocket is not { } s || MacUidOffset(list.Slice(s.Start, s.Length), self, pid) is not { } at)
        {
            return (null, null);
        }

        return clientSocket is { } c && c.Length >= at + 4 ? (BitConverter.ToUInt32(list[(c.Start + at)..]), self) : (null, self);
    }

    // xsocket_n: so_uid then so_last_pid follow so_oobmark, at 68 and 72 laid out naturally, at 64 and 68 packed to 4 bytes.
    private static int? MacUidOffset(ReadOnlySpan<byte> socket, uint self, int pid)
    {
        foreach (int at in (int[])[68, 64])
        {
            if (socket.Length >= at + 8 && BitConverter.ToUInt32(socket[at..]) == self && BitConverter.ToInt32(socket[(at + 4)..]) == pid)
            {
                return at;
            }
        }

        // The last owner can be another process (a socket passed on); then only the account says which layout it is.
        bool natural = socket.Length >= 72 && BitConverter.ToUInt32(socket[68..]) == self;
        bool packed = socket.Length >= 68 && BitConverter.ToUInt32(socket[64..]) == self;
        return natural == packed ? null : natural ? 68 : 64;
    }

    // xinpcb_n: inp_fport at 16 and inp_lport at 18 (network order), then inp_vflag and the foreign and local addresses
    // (16 bytes each; an IPv4 address is the last 4): at 48, 52 and 68 laid out naturally, at 44, 48 and 64 packed to 4.
    private static bool MacConnects(ReadOnlySpan<byte> record, IPEndPoint local, IPEndPoint remote)
    {
        if (record.Length < 84 || BinaryPrimitives.ReadUInt16BigEndian(record[18..]) != local.Port
            || BinaryPrimitives.ReadUInt16BigEndian(record[16..]) != remote.Port)
        {
            return false;
        }

        foreach ((int flags, int foreign) in ((int, int)[])[(48, 52), (44, 48)])
        {
            int at = record[flags] switch
            {
                var f when (f & 0x1) != 0 => 12, // INP_IPV4
                var f when (f & 0x2) != 0 => 0, // INP_IPV6
                _ => -1,
            };
            if (at < 0)
            {
                continue;
            }

            int size = at == 12 ? 4 : 16;
            IPAddress remoteAddress = new(record.Slice(foreign + at, size)), localAddress = new(record.Slice(foreign + 16 + at, size));
            if (Normalize(new IPEndPoint(localAddress, local.Port)).Equals(local) && Normalize(new IPEndPoint(remoteAddress, remote.Port)).Equals(remote))
            {
                return true;
            }
        }

        return false;
    }

    private static int RoundUp(uint length) => (int)((length + 7) & ~7u);

    [DllImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SysctlByName(byte[] name, byte[]? value, ref nuint length, IntPtr newValue, nuint newLength);

    // Windows: the TCP table names the process that owns each end; its token names the account.
    [SupportedOSPlatform("windows")]
    private static LocalAccount Windows(IPEndPoint client, IPEndPoint server)
    {
        (int? clientPid, int? serverPid) = (null, null);
        foreach (bool v6 in (bool[])[false, true])
        {
            (int? Client, int? Server) found = WindowsOwners(WindowsTable(v6), v6, client, server);
            (clientPid, serverPid) = (clientPid ?? found.Client, serverPid ?? found.Server);
        }

        return WindowsAccount(clientPid, serverPid, WindowsAccountOf);
    }

    /// <summary>
    /// Unavailable when the table does not list the router's own end; Unknown when it does not list the client's, or
    /// names no process for it (pid 0: TIME_WAIT, a connection the client already closed), or that process cannot be
    /// read; otherwise the account of the client's process.
    /// </summary>
    internal static LocalAccount WindowsAccount(int? clientPid, int? serverPid, Func<int, LocalAccount> accountOf)
    {
        if (serverPid is null)
        {
            return LocalAccount.Unavailable;
        }

        if (clientPid is not { } pid || pid <= 0)
        {
            return LocalAccount.Unknown;
        }

        if (pid == Environment.ProcessId || pid == serverPid)
        {
            return LocalAccount.Same;
        }

        try
        {
            return accountOf(pid);
        }
        catch (Exception ex) when (ex is Win32Exception or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            return LocalAccount.Unknown;
        }
    }

    // MIB_TCPTABLE_OWNER_PID: a count, then rows of state, local address, local port, remote address, remote port and owning
    // process (24 bytes). MIB_TCP6TABLE_OWNER_PID rows: local address (16), scope, port, remote address (16), scope, port,
    // state and owning process (56 bytes). Addresses are in network order; a port is the first two bytes of its DWORD.
    internal static (int? Client, int? Server) WindowsOwners(ReadOnlySpan<byte> table, bool v6, IPEndPoint client, IPEndPoint server)
    {
        int? clientPid = null, serverPid = null;
        if (table.Length < 4)
        {
            return (null, null);
        }

        int rows = BitConverter.ToInt32(table), size = v6 ? 56 : 24;
        for (int i = 0; i < rows && 4 + ((i + 1) * size) <= table.Length; i++)
        {
            ReadOnlySpan<byte> row = table.Slice(4 + (i * size), size);
            (IPEndPoint local, IPEndPoint remote, int pid) = v6
                ? (WindowsEndPoint(row[..16], row[20..]), WindowsEndPoint(row.Slice(24, 16), row[44..]), BitConverter.ToInt32(row[52..]))
                : (WindowsEndPoint(row.Slice(4, 4), row[8..]), WindowsEndPoint(row.Slice(12, 4), row[16..]), BitConverter.ToInt32(row[20..]));
            if (local.Equals(client) && remote.Equals(server))
            {
                clientPid = pid;
            }
            else if (local.Equals(server) && remote.Equals(client))
            {
                serverPid = pid;
            }
        }

        return (clientPid, serverPid);
    }

    private static IPEndPoint WindowsEndPoint(ReadOnlySpan<byte> address, ReadOnlySpan<byte> port) =>
        Normalize(new IPEndPoint(new IPAddress(address), BinaryPrimitives.ReadUInt16BigEndian(port)));

    [SupportedOSPlatform("windows")]
    private static byte[] WindowsTable(bool v6)
    {
        const uint InsufficientBuffer = 122;
        const int OwnerPidConnections = 4; // TCP_TABLE_OWNER_PID_CONNECTIONS
        uint family = v6 ? 23u : 2u;
        uint size = 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            IntPtr buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal((int)size);
            try
            {
                uint result = GetExtendedTcpTable(buffer, ref size, order: false, family, OwnerPidConnections, 0);
                if (result == 0 && buffer != IntPtr.Zero)
                {
                    byte[] table = new byte[size];
                    Marshal.Copy(buffer, table, 0, (int)size);
                    return table;
                }

                if (result != InsufficientBuffer)
                {
                    return [];
                }

                size += 4096; // connections opened meanwhile
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        return [];
    }

    [SupportedOSPlatform("windows")]
    private static LocalAccount WindowsAccountOf(int pid)
    {
        const uint QueryLimitedInformation = 0x1000, TokenQuery = 0x0008, AccessDenied = 5;
        IntPtr process = OpenProcess(QueryLimitedInformation, inherit: false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            // Another user's process is closed to this one; a process that is gone says something else.
            return Marshal.GetLastPInvokeError() == AccessDenied ? LocalAccount.Other : LocalAccount.Unknown;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out IntPtr token))
            {
                return Marshal.GetLastPInvokeError() == AccessDenied ? LocalAccount.Other : LocalAccount.Unknown;
            }

            try
            {
                using var owner = new WindowsIdentity(token);
                using WindowsIdentity self = WindowsIdentity.GetCurrent();
                return owner.User is { } user && (user == self.User || user.IsWellKnown(WellKnownSidType.LocalSystemSid))
                    ? LocalAccount.Same
                    : LocalAccount.Other;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>An IPv4 address seen through a dual-stack socket (<c>::ffff:127.0.0.1</c>) as the IPv4 address it is.</summary>
    private static IPEndPoint Normalize(IPEndPoint endPoint) =>
        endPoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port) : endPoint;
}
