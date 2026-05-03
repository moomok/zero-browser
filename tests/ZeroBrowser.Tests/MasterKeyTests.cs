using FluentAssertions;
using Xunit;
using ZeroBrowser.Storage.Crypto;

namespace ZeroBrowser.Tests;

public class MasterKeyTests : IDisposable
{
    private readonly string _path;

    public MasterKeyTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"zb-mk-{Guid.NewGuid():N}.bin");
    }

    [Fact]
    public void Initialize_then_unlock_with_same_password_succeeds()
    {
        var mk = new MasterKey(_path);
        var setupBox = mk.Initialize("hunter2");
        setupBox.Should().NotBeNull();
        mk.Exists.Should().BeTrue();

        var unlockBox = mk.TryUnlock("hunter2");
        unlockBox.Should().NotBeNull();
    }

    [Fact]
    public void Wrong_password_returns_null()
    {
        var mk = new MasterKey(_path);
        mk.Initialize("correct-horse");

        mk.TryUnlock("wrong").Should().BeNull();
        mk.TryUnlock("Correct-Horse").Should().BeNull();  // case-sensitive
        mk.TryUnlock("").Should().BeNull();
    }

    [Fact]
    public void Unlocked_box_encrypts_and_decrypts_payloads()
    {
        var mk = new MasterKey(_path);
        var box = mk.Initialize("p4ssw0rd!");
        var ciphertext = box.EncryptString("secret proxy password");

        var box2 = mk.TryUnlock("p4ssw0rd!");
        box2.Should().NotBeNull();
        box2!.DecryptString(ciphertext).Should().Be("secret proxy password");
    }

    [Fact]
    public void TryUnlock_returns_null_when_file_does_not_exist()
    {
        var mk = new MasterKey(_path);
        mk.Exists.Should().BeFalse();
        mk.TryUnlock("anything").Should().BeNull();
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
