namespace ZeroBrowser.Core.Models;

public enum OperatingSystemKind
{
    Windows10,
    Windows11,
    MacOS,
    Linux
}

public enum WebRtcMode
{
    Real,
    Proxy,
    Disabled,
    Fake
}

public enum ProxyType
{
    Http,
    Https,
    Socks5,
    /// <summary>Tor SOCKS5 (127.0.0.1:9050 by default). Managed by <c>TorManager</c>;
    /// each profile gets isolated circuits via unique SOCKS5 credentials.</summary>
    Tor
}
