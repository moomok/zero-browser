using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using ZeroBrowser.Core.Models;
using ZeroBrowser.Storage.Sqlite;

namespace ZeroBrowser.Tests;

public class ProfileExtensionRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ProfileRepository _repo;
    private readonly Profile _parent;

    public ProfileExtensionRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zb-ext-{Guid.NewGuid():N}.db");
        _repo = new ProfileRepository(_dbPath);
        _parent = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Owner",
            FingerprintSeed = "seed",
            StoragePath = Path.Combine(Path.GetTempPath(), "zb-ext-storage", Guid.NewGuid().ToString("N"))
        };
        _repo.Insert(_parent);
    }

    [Fact]
    public void Insert_and_list_round_trips()
    {
        var ext = new ProfileExtension
        {
            Id = Guid.NewGuid(),
            ProfileId = _parent.Id,
            Name = "uBlock Origin",
            Path = "/tmp/extensions/ublock",
            Enabled = true,
            SortOrder = 0
        };
        _repo.InsertExtension(ext);

        var listed = _repo.ListExtensions(_parent.Id);
        listed.Should().HaveCount(1);
        listed[0].Name.Should().Be("uBlock Origin");
        listed[0].Path.Should().Be("/tmp/extensions/ublock");
        listed[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Update_persists_toggle_and_rename()
    {
        var ext = new ProfileExtension
        {
            Id = Guid.NewGuid(), ProfileId = _parent.Id,
            Name = "Original", Path = "/p/orig", Enabled = true, SortOrder = 0
        };
        _repo.InsertExtension(ext);

        ext.Name = "Renamed";
        ext.Enabled = false;
        ext.SortOrder = 5;
        _repo.UpdateExtension(ext);

        var listed = _repo.ListExtensions(_parent.Id).Single();
        listed.Name.Should().Be("Renamed");
        listed.Enabled.Should().BeFalse();
        listed.SortOrder.Should().Be(5);
    }

    [Fact]
    public void Delete_removes_extension()
    {
        var ext = new ProfileExtension
        {
            Id = Guid.NewGuid(), ProfileId = _parent.Id,
            Name = "Tmp", Path = "/x", Enabled = true, SortOrder = 0
        };
        _repo.InsertExtension(ext);
        _repo.DeleteExtension(ext.Id);

        _repo.ListExtensions(_parent.Id).Should().BeEmpty();
    }

    [Fact]
    public void Cascade_delete_removes_extensions_when_profile_dropped()
    {
        for (var i = 0; i < 3; i++)
        {
            _repo.InsertExtension(new ProfileExtension
            {
                Id = Guid.NewGuid(), ProfileId = _parent.Id,
                Name = $"E{i}", Path = $"/p{i}", Enabled = true, SortOrder = i
            });
        }
        _repo.ListExtensions(_parent.Id).Should().HaveCount(3);

        _repo.Delete(_parent.Id);
        _repo.ListExtensions(_parent.Id).Should().BeEmpty();
    }

    [Fact]
    public void List_orders_by_sort_order_then_created_at()
    {
        var ids = new List<Guid>();
        foreach (var (sort, label) in new[] { (2, "Third"), (0, "First"), (1, "Second") })
        {
            var ext = new ProfileExtension
            {
                Id = Guid.NewGuid(), ProfileId = _parent.Id,
                Name = label, Path = "/" + label, Enabled = true, SortOrder = sort
            };
            ids.Add(ext.Id);
            _repo.InsertExtension(ext);
        }

        var listed = _repo.ListExtensions(_parent.Id);
        listed.Select(e => e.Name).Should().ContainInOrder("First", "Second", "Third");
    }

    [Fact]
    public void Profile_engine_path_round_trips()
    {
        var p = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Engine Test",
            FingerprintSeed = "x",
            StoragePath = "/tmp/zb-engine-test",
            EnginePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe"
        };
        _repo.Insert(p);

        var loaded = _repo.Get(p.Id);
        loaded.Should().NotBeNull();
        loaded!.EnginePath.Should().Be(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
    }

    [Fact]
    public void Profile_engine_path_can_be_cleared_via_update()
    {
        var p = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Engine Clear",
            FingerprintSeed = "x",
            StoragePath = "/tmp/zb-clear-engine",
            EnginePath = "/usr/bin/google-chrome"
        };
        _repo.Insert(p);

        p.EnginePath = null;
        _repo.Update(p);

        _repo.Get(p.Id)!.EnginePath.Should().BeNull();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch (IOException) { /* best-effort */ }
        }
    }
}
