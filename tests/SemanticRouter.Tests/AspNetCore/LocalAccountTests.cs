using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Which account on this machine opened a loopback connection, read from the operating system's TCP tables.</summary>
public sealed class LocalAccountLookupTests
{
    private static readonly IPEndPoint Client = new(IPAddress.Loopback, 51234);
    private static readonly IPEndPoint Server = new(IPAddress.Loopback, 5080);

    [Fact]
    public void A_connection_this_process_opens_to_itself_belongs_to_its_own_account()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        using Socket accepted = listener.AcceptSocket();

        var clientEnd = (IPEndPoint)client.Client.LocalEndPoint!;
        var serverEnd = (IPEndPoint)accepted.LocalEndPoint!;
        LocalAccount account = LocalAccounts.Find(clientEnd, serverEnd);

        account.Should().Be(
            OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? LocalAccount.Same : LocalAccount.Unknown,
            MacRecords(clientEnd.Port, serverEnd.Port));
    }

    [Fact]
    public void An_ipv4_client_of_a_dual_stack_listener_is_found_by_its_ipv4_address()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.IPv6Any, 0);
        listener.Server.DualMode = true;
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        client.Connect(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port));
        using Socket accepted = listener.AcceptSocket();

        // As Kestrel reports it: the client as ::ffff:127.0.0.1.
        var remote = (IPEndPoint)accepted.RemoteEndPoint!;
        var local = (IPEndPoint)accepted.LocalEndPoint!;
        LocalAccounts.Find(remote, local).Should().Be(
            OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? LocalAccount.Same : LocalAccount.Unknown,
            MacRecords(remote.Port, local.Port));
    }

    [Theory]
    [InlineData(1000u, 1000u, "Same")]
    [InlineData(1001u, 1000u, "Other")]
    [InlineData(0u, 1000u, "Same")] // root can read the router's keys anyway
    [InlineData(null, 1000u, "Unknown")]
    [InlineData(1001u, null, "Unknown")]
    public void Both_ends_are_compared(uint? client, uint? server, string expected) =>
        LocalAccounts.Compare((client, server)).ToString().Should().Be(expected);

    [Fact]
    public void Linux_rows_name_the_owner_of_each_end()
    {
        string directory = Directory.CreateTempSubdirectory("router-proc").FullName;
        try
        {
            string tcp = Path.Combine(directory, "tcp"), tcp6 = Path.Combine(directory, "tcp6");
            File.WriteAllLines(tcp,
            [
                "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode",
                Row(0, "0100007F:13D8", "00000000:0000", "0A", 1000), // the router's listener
                Row(1, "0100007F:C822", "0100007F:13D8", "01", 1001), // another user's client
                Row(2, "0100007F:13D8", "0100007F:C822", "01", 1000), // the router's end of it
            ]);
            File.WriteAllLines(tcp6, ["  sl  local_address                         remote_address                        st"]);

            LocalAccounts.LinuxOwners([tcp, tcp6], Client, Server).Should().Be(((uint?)1001, (uint?)1000));
            LocalAccounts.Compare(LocalAccounts.LinuxOwners([tcp, tcp6], Client, Server)).Should().Be(LocalAccount.Other);
            LocalAccounts.LinuxOwners([tcp], new IPEndPoint(IPAddress.Loopback, 1), Server).Should().Be(((uint?)null, (uint?)null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linux_ipv6_rows_are_read_word_by_word_and_mapped_addresses_count_as_ipv4()
    {
        string loopback6 = BitConverter.IsLittleEndian ? "00000000000000000000000001000000" : "00000000000000000000000000000001";
        string mapped = BitConverter.IsLittleEndian ? "0000000000000000FFFF00000100007F" : "00000000000000000000FFFF7F000001";

        LocalAccounts.LinuxRow(Row(0, loopback6 + ":C822", loopback6 + ":13D8", "01", 7))!.Value
            .Should().Be((new IPEndPoint(IPAddress.IPv6Loopback, 51234), new IPEndPoint(IPAddress.IPv6Loopback, 5080), 7u));
        LocalAccounts.LinuxRow(Row(0, mapped + ":C822", mapped + ":13D8", "01", 7))!.Value.Local.Should().Be(Client);
        LocalAccounts.LinuxRow("  sl  local_address rem_address   st").Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_tables_name_the_process_that_owns_each_end(bool v6)
    {
        IPAddress address = v6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        IPEndPoint client = new(address, 51234), server = new(address, 5080);
        byte[] table = WindowsTable(v6, (server, new IPEndPoint(v6 ? IPAddress.IPv6Any : IPAddress.Any, 0), 4100), (client, server, 2200), (server, client, 4100));

        LocalAccounts.WindowsOwners(table, v6, client, server).Should().Be(((int?)2200, (int?)4100));
        LocalAccounts.WindowsOwners(table, v6, new IPEndPoint(address, 1), server).Should().Be(((int?)null, (int?)null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_macos_socket_list_names_the_owner_of_each_end(bool packed)
    {
        var other6 = (new IPEndPoint(IPAddress.IPv6Loopback, 9), new IPEndPoint(IPAddress.IPv6Loopback, 10));
        byte[] list = MacList(packed, (Client, Server, 501u, 900), (Server, Client, 502u, 4242), (other6.Item1, other6.Item2, 0u, 1));

        LocalAccounts.MacOwners(list, Client, Server, self: 502, pid: 4242).Should().Be(((uint?)501, (uint?)502));
        LocalAccounts.MacOwners(list, Client, Server, self: 503, pid: 4242).Should().Be(((uint?)null, (uint?)null), "the router's own end must show the layout");
        LocalAccounts.MacOwners(list, new IPEndPoint(IPAddress.Loopback, 1), Server, self: 502, pid: 4242).Should().Be(((uint?)null, (uint?)null));
        LocalAccounts.MacOwners(list.AsSpan(0, 10), Client, Server, self: 502, pid: 4242).Should().Be(((uint?)null, (uint?)null));
    }

    private static string Row(int sl, string local, string remote, string state, uint uid) =>
        $"   {sl}: {local} {remote} {state} 00000000:00000000 00:00000000 00000000  {uid}        0 1234 1 0000000000000000 20 4 30 10 -1";

    private static byte[] WindowsTable(bool v6, params (IPEndPoint Local, IPEndPoint Remote, int Pid)[] rows)
    {
        int size = v6 ? 56 : 24;
        byte[] table = new byte[4 + (rows.Length * size)];
        BinaryPrimitives.WriteInt32LittleEndian(table, rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            Span<byte> row = table.AsSpan(4 + (i * size), size);
            if (v6)
            {
                rows[i].Local.Address.GetAddressBytes().CopyTo(row);
                BinaryPrimitives.WriteUInt16BigEndian(row[20..], (ushort)rows[i].Local.Port);
                rows[i].Remote.Address.GetAddressBytes().CopyTo(row[24..]);
                BinaryPrimitives.WriteUInt16BigEndian(row[44..], (ushort)rows[i].Remote.Port);
                BinaryPrimitives.WriteInt32LittleEndian(row[48..], 5);
                BinaryPrimitives.WriteInt32LittleEndian(row[52..], rows[i].Pid);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(row, 5);
                rows[i].Local.Address.GetAddressBytes().CopyTo(row[4..]);
                BinaryPrimitives.WriteUInt16BigEndian(row[8..], (ushort)rows[i].Local.Port);
                rows[i].Remote.Address.GetAddressBytes().CopyTo(row[12..]);
                BinaryPrimitives.WriteUInt16BigEndian(row[16..], (ushort)rows[i].Remote.Port);
                BinaryPrimitives.WriteInt32LittleEndian(row[20..], rows[i].Pid);
            }
        }

        return table;
    }

    // The layout netstat reads: xinpgen, then per socket xinpcb_n, xsocket_n, two xsockbuf_n, xsockstat_n and xtcpcb_n,
    // then a closing xinpgen. Packed to 4 bytes, the fields after the first 64-bit one move up by 4.
    private static byte[] MacList(bool packed, params (IPEndPoint Local, IPEndPoint Remote, uint Uid, int Pid)[] sockets)
    {
        int shift = packed ? 4 : 0;
        using var stream = new MemoryStream();
        Write(stream, Record(24, 0, _ => { }));
        foreach ((IPEndPoint local, IPEndPoint remote, uint uid, int pid) in sockets)
        {
            Write(stream, Record(104 - shift, 0x10, r =>
            {
                BinaryPrimitives.WriteUInt16BigEndian(r[16..], (ushort)remote.Port);
                BinaryPrimitives.WriteUInt16BigEndian(r[18..], (ushort)local.Port);
                bool v4 = local.AddressFamily == AddressFamily.InterNetwork;
                r[48 - shift] = v4 ? (byte)0x1 : (byte)0x2;
                remote.Address.GetAddressBytes().CopyTo(r[((v4 ? 64 : 52) - shift)..]);
                local.Address.GetAddressBytes().CopyTo(r[((v4 ? 80 : 68) - shift)..]);
            }));
            Write(stream, Record(112 - (2 * shift), 0x01, r =>
            {
                BitConverter.TryWriteBytes(r[(68 - shift)..], uid);
                BitConverter.TryWriteBytes(r[(72 - shift)..], pid);
            }));
            Write(stream, Record(32, 0x02, _ => { }));
            Write(stream, Record(32, 0x04, _ => { }));
            Write(stream, Record(136, 0x08, _ => { }));
            Write(stream, Record(232, 0x20, _ => { }));
        }

        Write(stream, Record(24, 0, _ => { }));
        return stream.ToArray();

        static void Write(Stream stream, byte[] record) => stream.Write(record);

        static byte[] Record(int length, uint kind, SpanAction fill)
        {
            byte[] record = new byte[(length + 7) & ~7];
            BitConverter.TryWriteBytes(record, (uint)length);
            BitConverter.TryWriteBytes(record.AsSpan(4), kind);
            fill(record.AsSpan(0, length));
            return record;
        }
    }

    /// <summary>On macOS, the records of the sockets on these ports, in hex, to tell which layout a failing kernel uses.</summary>
    private static string MacRecords(params int[] ports)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return string.Empty;
        }

        byte[]? list = LocalAccounts.MacPcbList();
        if (list is null)
        {
            return "net.inet.tcp.pcblist_n could not be read";
        }

        var text = new System.Text.StringBuilder($"pcblist_n: {list.Length} bytes; ");
        var group = new List<string>();
        bool keep = false;
        for (int offset = (int)((BitConverter.ToUInt32(list) + 7) & ~7u); offset + 8 <= list.Length;)
        {
            uint length = BitConverter.ToUInt32(list.AsSpan(offset)), kind = BitConverter.ToUInt32(list.AsSpan(offset + 4));
            if (length <= 24 || offset + length > list.Length)
            {
                break;
            }

            if (kind == 0x10)
            {
                keep = ports.Contains(BinaryPrimitives.ReadUInt16BigEndian(list.AsSpan(offset + 18)));
            }

            group.Add($"kind {kind:X} len {length}: {Convert.ToHexString(list, offset, (int)Math.Min(length, 120u))}");
            if (kind == 0x20)
            {
                if (keep)
                {
                    text.AppendJoin(" | ", group).Append(" || ");
                }

                (group, keep) = ([], false);
            }

            offset += (int)((length + 7) & ~7u);
        }

        return text.ToString();
    }

    private delegate void SpanAction(Span<byte> record);
}

/// <summary>Another account on this machine is a remote client: for the dashboard, the gateway and the CLI connectors.</summary>
public sealed class OtherLocalAccountTests : RouterHostTest
{
    private Task<HttpClient> StartAsync(LocalAccount caller, bool trustOthers = false, Action<PromptRouterMiddlewareOptions>? middleware = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                middleware?.Invoke(o);
            },
            services: s => s.Configure<NetworkOptions>(n => n.TrustOtherLocalAccounts = trustOthers),
            beforeRouter: app => app.Use((context, next) =>
            {
                // A loopback connection as Kestrel reports it, and who the operating system says opened it.
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                context.Connection.RemotePort = 51234;
                context.Connection.LocalIpAddress = IPAddress.Loopback;
                context.Connection.LocalPort = 5080;
                context.Features.Set<IConnectionItemsFeature>(new ConnectionItems());
                LocalAccounts.Remember(context, caller);
                return next(context);
            }));

    [Theory]
    [InlineData("Same")]
    [InlineData("Unknown")]
    public async Task The_routers_own_account_is_this_machine(string caller)
    {
        HttpClient client = await StartAsync(Enum.Parse<LocalAccount>(caller));

        (await client.GetAsync("/router/api/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Another_account_needs_a_token_and_a_gateway_key()
    {
        HttpClient client = await StartAsync(LocalAccount.Other);

        HttpResponseMessage dashboard = await client.GetAsync("/router/api/status");
        dashboard.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await dashboard.Content.ReadAsStringAsync()).Should().Contain("another account on this machine");

        HttpResponseMessage gateway = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        gateway.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await gateway.Content.ReadAsStringAsync()).Should().Contain("another account on this machine");
    }

    [Fact]
    public async Task Another_account_with_a_gateway_key_is_served_as_a_remote_client()
    {
        HttpClient client = await StartAsync(LocalAccount.Other, middleware: o => o.GatewayKeys["colleague"] = "gw-colleague-key-123");
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Json(ChatJson) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "gw-colleague-key-123");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Other_accounts_can_be_trusted_on_purpose()
    {
        HttpClient client = await StartAsync(LocalAccount.Other, trustOthers: true);

        (await client.GetAsync("/router/api/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void A_request_marked_remote_is_not_local_even_without_an_address()
    {
        var context = new DefaultHttpContext();
        DashboardAccess.IsLocal(context).Should().BeTrue("in-process callers have no address");

        DashboardAccess.MarkRemote(context);

        DashboardAccess.IsLocal(context).Should().BeFalse();
    }

    private sealed class ConnectionItems : IConnectionItemsFeature
    {
        public IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
    }
}
