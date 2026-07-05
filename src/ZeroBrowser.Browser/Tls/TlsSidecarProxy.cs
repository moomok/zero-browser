using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ZeroBrowser.Core.Models;

namespace ZeroBrowser.Browser.Tls;

/// <summary>
/// Lightweight HTTP CONNECT proxy that performs TLS ClientHello spoofing on the outbound
/// leg via .NET CipherSuitesPolicy. Runs in-process — no external Go binary needed.
/// 
/// Created per profile to give each browser instance a unique TLS/JA3 fingerprint.
/// 
/// Architecture:
///   Chromium → HTTP CONNECT → TlsSidecarProxy (Leg A, plain HTTP)
///                           → TCP connect to target
///                           → SslStream with custom CipherSuitesPolicy (Leg B, TLS)
///                           → bidirectional pipe
/// </summary>
public sealed class TlsSidecarProxy : IAsyncDisposable
{
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private TcpListener? _listener;

    private readonly TlsCipherSuite[] _cipherSuites;
    private readonly SslProtocols _sslProtocols;

    public int Port => _port;
    public bool IsRunning => _listenTask is { IsCompleted: false };

    private TlsSidecarProxy(int port, TlsCipherSuite[] cipherSuites, SslProtocols protocols)
    {
        _port = port;
        _cipherSuites = cipherSuites;
        _sslProtocols = protocols;
    }

    /// <summary>
    /// Create a proxy on a random available port with a TLS fingerprint derived from a seed.
    /// </summary>
    public static async Task<TlsSidecarProxy> CreateAsync(string seed, string tlsMode, CancellationToken ct = default)
    {
        var port = FindFreePort();
        var suites = TlsFingerprintPool.GetCipherSuites(seed, tlsMode);
        var protocols = SslProtocols.Tls13 | SslProtocols.Tls12;
        var proxy = new TlsSidecarProxy(port, suites, protocols);
        await proxy.StartAsync(ct);
        return proxy;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private Task StartAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _listenTask = AcceptLoopAsync(ct);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleConnectionAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                using var clientStream = client.GetStream();
                using var reader = new StreamReader(clientStream, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync();
                if (requestLine is null) return;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHttpResponse(clientStream, 400, "Bad Request");
                    return;
                }

                var hostPort = parts[1].Split(':');
                var host = hostPort[0];
                var port = hostPort.Length > 1 && int.TryParse(hostPort[1], out var p) ? p : 443;

                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }

                using TcpClient target = new TcpClient();
                try
                {
                    await target.ConnectAsync(host, port, ct);
                }
                catch
                {
                    await WriteHttpResponse(clientStream, 502, "Cannot connect to target");
                    return;
                }

                await WriteHttpResponse(clientStream, 200, "Connection Established");

                if (port == 443)
                {
                    var sslOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        EnabledSslProtocols = _sslProtocols,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        CipherSuitesPolicy = new CipherSuitesPolicy(_cipherSuites),
                        RemoteCertificateValidationCallback = (_, _, _, _) => true
                    };
                    using var sslStream = new SslStream(target.GetStream(), false);
                    await sslStream.AuthenticateAsClientAsync(sslOptions, ct);
                    await PipeStreamToSsl(clientStream, sslStream, ct);
                }
                else
                {
                    using var targetStream = target.GetStream();
                    await BidirectionalPipeAsync(clientStream, targetStream, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TlsSidecarProxy connection error: {ex.Message}");
        }
    }

    private static async Task BidirectionalPipeAsync(NetworkStream a, NetworkStream b, CancellationToken ct)
    {
        var copyTask1 = a.CopyToAsync(b, ct);
        var copyTask2 = b.CopyToAsync(a, ct);
        await Task.WhenAny(copyTask1, copyTask2);
    }

    private static async Task PipeStreamToSsl(NetworkStream a, SslStream b, CancellationToken ct)
    {
        var copyTask1 = a.CopyToAsync(b, ct);
        var copyTask2 = b.CopyToAsync(a, ct);
        await Task.WhenAny(copyTask1, copyTask2);
    }

    private static async Task WriteHttpResponse(NetworkStream stream, int statusCode, string statusText)
    {
        var response = $"HTTP/1.1 {statusCode} {statusText}\r\n\r\n";
        var bytes = System.Text.Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        if (_listenTask is not null)
        {
            try { await _listenTask; } catch { }
        }
        _cts.Dispose();
    }
}