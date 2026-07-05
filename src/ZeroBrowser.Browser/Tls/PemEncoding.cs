namespace ZeroBrowser.Browser.Tls;

internal static class PemCertificateWriter
{
    public static string WritePEM(string label, ReadOnlySpan<char> base64Data)
    {
        return $"-----BEGIN {label}-----\n{base64Data}\n-----END {label}-----\n";
    }
}