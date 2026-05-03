using FluentAssertions;
using Xunit;
using ZeroBrowser.Core.Models;
using ZeroBrowser.Storage.Crypto;
using ZeroBrowser.Storage.Sqlite;

namespace ZeroBrowser.Tests;

public class ProxyRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SecretBox _box;
    private readonly ProxyRepository _repo;

    public ProxyRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zb-proxy-{Guid.NewGuid():N}.db");
        _box = SecretBox.Derive("test-master", SecretBox.NewSalt());
        _repo = new ProxyRepository(_dbPath, _box);
    }

    [Fact]
    public void Insert_and_list_round_trips_password()
    {
        var p = new ProxyEntry
        {
            Id = Guid.NewGuid(),
            Type = ProxyType.Http,
            Host = "1.2.3.4",
            Port = 8080,
            Username = "user1",
            Password = "p4ssw0rd"
        };
        _repo.Insert(p);

        var loaded = _repo.ListAll();
        loaded.Should().HaveCount(1);
        loaded[0].Host.Should().Be("1.2.3.4");
        loaded[0].Port.Should().Be(8080);
        loaded[0].Username.Should().Be("user1");
        loaded[0].Password.Should().Be("p4ssw0rd");
        loaded[0].Type.Should().Be(ProxyType.Http);
    }

    [Fact]
    public void Password_is_encrypted_at_rest()
    {
        var p = new ProxyEntry
        {
            Id = Guid.NewGuid(),
            Type = ProxyType.Socks5,
            Host = "secret.example.com",
            Port = 1080,
            Username = "user",
            Password = "very-secret-password-xyz"
        };
        _repo.Insert(p);

        var fileBytes = File.ReadAllBytes(_dbPath);
        var asString = System.Text.Encoding.UTF8.GetString(fileBytes);
        asString.Should().NotContain("very-secret-password-xyz");
    }

    [Fact]
    public void Bulk_insert_works()
    {
        var input = new List<ProxyEntry>();
        for (int i = 0; i < 50; i++)
        {
            input.Add(new ProxyEntry
            {
                Id = Guid.NewGuid(),
                Type = ProxyType.Http,
                Host = $"10.0.0.{i}",
                Port = 8000 + i,
                Username = $"u{i}",
                Password = $"p{i}"
            });
        }
        _repo.InsertMany(input);
        _repo.ListAll().Should().HaveCount(50);
    }

    [Fact]
    public void Delete_removes_from_listing()
    {
        var p = new ProxyEntry
        {
            Id = Guid.NewGuid(), Type = ProxyType.Http,
            Host = "x", Port = 1, Password = null, Username = null
        };
        _repo.Insert(p);
        _repo.Delete(p.Id);
        _repo.ListAll().Should().BeEmpty();
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
