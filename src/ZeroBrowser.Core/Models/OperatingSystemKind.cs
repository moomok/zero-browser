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
    Socks5
}
