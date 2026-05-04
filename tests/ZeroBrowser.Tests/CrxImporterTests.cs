using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Xunit;
using ZeroBrowser.Core.Util;

namespace ZeroBrowser.Tests;

public class CrxImporterTests : IDisposable
{
    private readonly string _tempRoot;

    public CrxImporterTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"zb-crx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Extracts_crx_v3_with_manifest()
    {
        var crxPath = Path.Combine(_tempRoot, "test.crx");
        BuildSyntheticCrx(crxPath, version: 3, manifestJson: """{"name":"My Test Ext","version":"1.0","manifest_version":3}""");

        var dest = Path.Combine(_tempRoot, "extracted");
        var result = CrxImporter.Extract(crxPath, dest);

        result.Should().Be(dest);
        File.Exists(Path.Combine(dest, "manifest.json")).Should().BeTrue();
        File.ReadAllText(Path.Combine(dest, "manifest.json")).Should().Contain("My Test Ext");
    }

    [Fact]
    public void Extracts_crx_v2_with_manifest()
    {
        var crxPath = Path.Combine(_tempRoot, "test_v2.crx");
        BuildSyntheticCrx(crxPath, version: 2, manifestJson: """{"name":"V2 Ext","version":"1.0"}""");

        var dest = Path.Combine(_tempRoot, "extracted_v2");
        CrxImporter.Extract(crxPath, dest);

        File.ReadAllText(Path.Combine(dest, "manifest.json")).Should().Contain("V2 Ext");
    }

    [Fact]
    public void Read_manifest_name_returns_name_field()
    {
        var dir = Path.Combine(_tempRoot, "named-ext");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """{"name":"My Named Ext","version":"1.0"}""");

        CrxImporter.ReadManifestName(dir).Should().Be("My Named Ext");
    }

    [Fact]
    public void Read_manifest_name_strips_msg_placeholders()
    {
        var dir = Path.Combine(_tempRoot, "i18n-ext");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """{"name":"__MSG_extName__","version":"1.0"}""");

        CrxImporter.ReadManifestName(dir).Should().Be("extName");
    }

    [Fact]
    public void Read_manifest_name_falls_back_to_dir_name()
    {
        var dir = Path.Combine(_tempRoot, "no-manifest-ext");
        Directory.CreateDirectory(dir);

        CrxImporter.ReadManifestName(dir).Should().Be("no-manifest-ext");
    }

    [Fact]
    public void Throws_on_missing_file()
    {
        var nonexistent = Path.Combine(_tempRoot, "does-not-exist.crx");
        var act = () => CrxImporter.Extract(nonexistent, Path.Combine(_tempRoot, "dest"));
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Throws_on_bad_magic()
    {
        var bad = Path.Combine(_tempRoot, "bad.crx");
        File.WriteAllBytes(bad, new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0x03, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00 });
        var act = () => CrxImporter.Extract(bad, Path.Combine(_tempRoot, "out"));
        act.Should().Throw<InvalidDataException>().WithMessage("*magic*");
    }

    [Fact]
    public void Throws_on_unsupported_version()
    {
        var bad = Path.Combine(_tempRoot, "v9.crx");
        var bytes = new List<byte>();
        bytes.AddRange("Cr24"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes((uint)9));   // version=9
        bytes.AddRange(BitConverter.GetBytes((uint)0));
        File.WriteAllBytes(bad, bytes.ToArray());
        var act = () => CrxImporter.Extract(bad, Path.Combine(_tempRoot, "out"));
        act.Should().Throw<InvalidDataException>().WithMessage("*Unsupported CRX version*");
    }

    [Fact]
    public void Throws_on_v2_header_with_uint_overflow_lengths()
    {
        // pubKeyLen=0xFFFFFFF0 and sigLen=0x4 used to overflow when cast to int
        // (int)0xFFFFFFF0 = -16, so 16 + (-16) + 4 = 4 — a positive offset
        // pointing into the CRX header itself, bypassing the bounds check.
        // After the fix, arithmetic happens in long space and rejects this.
        var bad = Path.Combine(_tempRoot, "overflow.crx");
        var bytes = new List<byte>();
        bytes.AddRange("Cr24"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes((uint)2));            // version 2
        bytes.AddRange(BitConverter.GetBytes(0xFFFFFFF0u));        // pubKeyLen
        bytes.AddRange(BitConverter.GetBytes(0x4u));               // sigLen
        // Pad with some bytes so file isn't empty.
        bytes.AddRange(new byte[64]);
        File.WriteAllBytes(bad, bytes.ToArray());
        var act = () => CrxImporter.Extract(bad, Path.Combine(_tempRoot, "out"));
        act.Should().Throw<InvalidDataException>().WithMessage("*past end of file*");
    }

    [Fact]
    public void Throws_when_archive_has_no_manifest()
    {
        var crxPath = Path.Combine(_tempRoot, "nomanifest.crx");
        // Build a CRX whose embedded ZIP only contains a non-manifest file.
        BuildSyntheticCrx(crxPath, version: 3, extraFiles: new[] { ("readme.txt", "hello") }, manifestJson: null);
        var act = () => CrxImporter.Extract(crxPath, Path.Combine(_tempRoot, "out"));
        act.Should().Throw<InvalidDataException>().WithMessage("*manifest.json*");
    }

    private static void BuildSyntheticCrx(
        string outPath,
        int version,
        string? manifestJson = """{"name":"x","version":"1.0"}""",
        IReadOnlyCollection<(string Name, string Content)>? extraFiles = null)
    {
        // 1. Build the inner ZIP archive in memory.
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (manifestJson is not null)
            {
                var entry = zip.CreateEntry("manifest.json");
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write(manifestJson);
            }
            foreach (var (name, content) in extraFiles ?? Array.Empty<(string, string)>())
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write(content);
            }
        }
        var zipBytes = zipStream.ToArray();

        // 2. Write the CRX header followed by the ZIP.
        using var fs = File.Create(outPath);
        fs.Write("Cr24"u8);
        fs.Write(BitConverter.GetBytes((uint)version));

        switch (version)
        {
            case 2:
                // pubKeyLen=0, sigLen=0 (synthetic, signature not validated)
                fs.Write(BitConverter.GetBytes((uint)0));
                fs.Write(BitConverter.GetBytes((uint)0));
                break;
            case 3:
                // headerLen=0 (synthetic)
                fs.Write(BitConverter.GetBytes((uint)0));
                break;
            default:
                throw new ArgumentException("Test only builds v2 or v3");
        }

        fs.Write(zipBytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }
}
