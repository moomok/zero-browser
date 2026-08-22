using System.Net.Sockets;
using System.Text;

namespace ZeroBrowser.Browser.Tor;

/// <summary>
/// Minimal SOCKS5 client (RFC 1928 + RFC 1929 username/password auth).
/// Used for exit-node IP checks through Tor and connectivity tests.
/// Only supports TCP CONNECT — enough for HTTP(S) requests over the tunnel.
/// </summary>
public static class Socks5Client
{
    public const byte MethodNoAuth = 0x00;
    public const byte MethodUserPass = 0x02;
    public const byte ReplySucceeded = 0x00;

    /// <summary>Result of a successful SOCKS5 CONNECT.</summary>
    public sealed record Socks5Connection(Stream Stream, byte ReplyCode) : IDisposable
    {
        public void Dispose() => Stream.Dispose();
    }

    public static async Task<Socks5Connection> ConnectAsync(
        string proxyHost,
        int proxyPort,
        string targetHost,
        int targetPort,
        string? username = null,
        string? password = null,
        CancellationToken ct = default)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxyHost, proxyPort, ct).ConfigureAwait(false);
            var stream = (Stream)client.GetStream();

            await HandshakeAsync(stream, username, password, ct).ConfigureAwait(false);
            var reply = await ConnectRequestAsync(stream, targetHost, targetPort, ct).ConfigureAwait(false);
            if (reply != ReplySucceeded)
            {
                throw new Socks5Exception($"SOCKS5 CONNECT failed (rep={ReplyName(reply)}).");
            }

            // Ownership of the TcpClient transfers to the returned NetworkStream lifetime;
            // keep the socket alive by detaching and wrapping it.
            var socket = client.Client;
            return new Socks5Connection(new NetworkStream(socket, ownsSocket: true), reply);
        }
        catch (Socks5Exception)
        {
            client.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new Socks5Exception($"SOCKS5 tunnel to {proxyHost}:{proxyPort} failed: {ex.Message}", ex);
        }
    }

    private static async Task HandshakeAsync(Stream s, string? username, string? password, CancellationToken ct)
    {
        bool wantAuth = !string.IsNullOrEmpty(username);
        // Greeting: VER=5, NMETHODS, then the methods we support.
        var greeting = new List<byte> { 0x05 };
        if (wantAuth)
        {
            greeting.AddRange(stackalloc byte[] { 2, MethodNoAuth, MethodUserPass });
        }
        else
        {
            greeting.AddRange(stackalloc byte[] { 1, MethodNoAuth });
        }
        await s.WriteAsync(greeting.ToArray(), ct).ConfigureAwait(false);

        var choice = await ReadExactlyAsync(s, 2, ct).ConfigureAwait(false);
        if (choice[0] != 0x05) throw new Socks5Exception("SOCKS5 greeting: bad version.");
        if (choice[1] == 0xFF) throw new Socks5Exception("SOCKS5: server rejected all auth methods.");

        if (choice[1] == MethodUserPass)
        {
            if (!wantAuth) throw new Socks5Exception("SOCKS5: server demands auth but none provided.");
            await UserPassAuthAsync(s, username!, password ?? "", ct).ConfigureAwait(false);
        }
        else if (choice[1] != MethodNoAuth)
        {
            throw new Socks5Exception($"SOCKS5: unsupported method {choice[1]}.");
        }
    }

    private static async Task UserPassAuthAsync(Stream s, string user, string pass, CancellationToken ct)
    {
        var u = Encoding.UTF8.GetBytes(user);
        var p = Encoding.UTF8.GetBytes(pass);
        if (u.Length > 255 || p.Length > 255) throw new Socks5Exception("SOCKS5: credentials too long.");

        var payload = new byte[3 + u.Length + p.Length];
        payload[0] = 0x01;                       // auth subnegotiation version
        payload[1] = (byte)u.Length;
        u.CopyTo(payload, 2);
        payload[2 + u.Length] = (byte)p.Length;
        p.CopyTo(payload, 3 + u.Length);

        await s.WriteAsync(payload, ct).ConfigureAwait(false);
        var resp = await ReadExactlyAsync(s, 2, ct).ConfigureAwait(false);
        if (resp[1] != 0x00) throw new Socks5Exception("SOCKS5: authentication failed.");
    }

    private static async Task<byte> ConnectRequestAsync(Stream s, string host, int port, CancellationToken ct)
    {
        var addrBytes = Encoding.UTF8.GetBytes(host);
        if (addrBytes.Length > 255) throw new Socks5Exception("SOCKS5: target hostname too long.");

        var req = new byte[7 + addrBytes.Length];
        req[0] = 0x05;                            // VER
        req[1] = 0x01;                            // CMD = CONNECT
        req[2] = 0x00;                            // RSV
        req[3] = 0x03;                            // ATYP = domain name
        req[4] = (byte)addrBytes.Length;
        addrBytes.CopyTo(req, 5);
        req[^2] = (byte)(port >> 8);
        req[^1] = (byte)(port & 0xFF);

        await s.WriteAsync(req, ct).ConfigureAwait(false);

        var head = await ReadExactlyAsync(s, 4, ct).ConfigureAwait(false);
        if (head[0] != 0x05) throw new Socks5Exception("SOCKS5 reply: bad version.");
        var bindAddrLen = head[3] switch
        {
            0x01 => 4,                            // IPv4
            0x03 => -1,                           // domain (length-prefixed)
            0x04 => 16,                           // IPv6
            _ => throw new Socks5Exception($"SOCKS5 reply: unknown ATYP {head[3]}.")
        };
        if (bindAddrLen == -1)
        {
            var lenByte = await ReadExactlyAsync(s, 1, ct).ConfigureAwait(false);
            bindAddrLen = lenByte[0];
        }
        // BND.ADDR + BND.PORT
        _ = await ReadExactlyAsync(s, bindAddrLen + 2, ct).ConfigureAwait(false);
        return head[1];
    }

    internal static async Task<byte[]> ReadExactlyAsync(Stream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException($"SOCKS5: stream closed after {read}/{count} bytes.");
            read += n;
        }
        return buf;
    }

    internal static string ReplyName(byte rep) => rep switch
    {
        0x00 => "succeeded",
        0x01 => "general failure",
        0x02 => "connection not allowed",
        0x03 => "network unreachable",
        0x04 => "host unreachable",
        0x05 => "connection refused",
        0x06 => "TTL expired",
        0x07 => "command not supported",
        0x08 => "address type not supported",
        _ => $"unknown({rep})"
    };
}

public sealed class Socks5Exception : Exception
{
    public Socks5Exception(string message) : base(message) { }
    public Socks5Exception(string message, Exception inner) : base(message, inner) { }
}
