using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Emby.Server.Implementations.Library;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using AudioBook = MediaBrowser.Controller.Entities.AudioBook;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class UserDataManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly UserDataManager _userDataManager;
    private readonly User _user;

    public UserDataManagerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);

        var config = new Mock<IServerConfigurationManager>();
        config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());

        _userDataManager = new UserDataManager(config.Object, factory.Object);
        _user = new User("user", "auth-provider", "reset-provider")
        {
            Id = Guid.NewGuid()
        };
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private JellyfinDbContext CreateDbContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private AudioBook CreateAudioBook()
    {
        // GetUserDataKeys(): ["Author-Series-0001Book Title", "<item id N>"]
        return new AudioBook
        {
            Id = Guid.NewGuid(),
            Name = "Book Title",
            Album = "Series",
            AlbumArtists = new[] { "Author" },
            IndexNumber = 1
        };
    }

    private UserData CreateUserDataRow(AudioBook item, string key, long positionTicks)
    {
        return new UserData
        {
            ItemId = item.Id,
            Item = null,
            UserId = _user.Id,
            User = null,
            CustomDataKey = key,
            PlaybackPositionTicks = positionTicks
        };
    }

    [Fact]
    public void GetUserData_RowsUnderCurrentAndRetiredKeys_PrefersCurrentKeyRow()
    {
        var item = CreateAudioBook();
        var currentKey = item.GetUserDataKeys()[0];

        // the retired-key row comes first to ensure selection is by key, not row order
        item.UserData = new List<UserData>
        {
            CreateUserDataRow(item, "Author-Old Album-0001Old File Name", 111),
            CreateUserDataRow(item, currentKey, 222)
        };

        var userData = _userDataManager.GetUserData(_user, item);

        Assert.NotNull(userData);
        Assert.Equal(currentKey, userData.Key);
        Assert.Equal(222, userData.PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserData_NoPrimaryKeyRow_UsesNextCurrentKeyRow()
    {
        var item = CreateAudioBook();
        var idKey = item.GetUserDataKeys()[1];

        item.UserData = new List<UserData>
        {
            CreateUserDataRow(item, "Author-Old Album-0001Old File Name", 111),
            CreateUserDataRow(item, idKey, 333)
        };

        var userData = _userDataManager.GetUserData(_user, item);

        Assert.NotNull(userData);
        Assert.Equal(idKey, userData.Key);
        Assert.Equal(333, userData.PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserData_OnlyRetiredKeyRows_ReturnsRetiredKeyRow()
    {
        var item = CreateAudioBook();

        item.UserData = new List<UserData>
        {
            CreateUserDataRow(item, "Author-Old Album-0001Old File Name", 111)
        };

        var userData = _userDataManager.GetUserData(_user, item);

        Assert.NotNull(userData);
        Assert.Equal(111, userData.PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserData_NoRows_ReturnsDefaultWithPrimaryKey()
    {
        var item = CreateAudioBook();
        item.UserData = new List<UserData>();

        var userData = _userDataManager.GetUserData(_user, item);

        Assert.NotNull(userData);
        Assert.Equal(item.GetUserDataKeys()[0], userData.Key);
        Assert.Equal(0, userData.PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserData_RowsForOtherUsers_AreIgnored()
    {
        var item = CreateAudioBook();
        var currentKey = item.GetUserDataKeys()[0];

        var otherUserRow = CreateUserDataRow(item, currentKey, 999);
        otherUserRow.UserId = Guid.NewGuid();

        item.UserData = new List<UserData>
        {
            otherUserRow,
            CreateUserDataRow(item, currentKey, 222)
        };

        var userData = _userDataManager.GetUserData(_user, item);

        Assert.NotNull(userData);
        Assert.Equal(222, userData.PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserDataBatch_DatabaseFallback_ResolvesRowsByKeyOrder()
    {
        // no preloaded navigation data, so the batch takes the database fallback
        var fossilItem = CreateAudioBook();
        var retiredItem = CreateAudioBook();

        using (var ctx = CreateDbContext())
        {
            ctx.Users.Add(_user);
            ctx.BaseItems.Add(new BaseItemEntity { Id = fossilItem.Id, Type = typeof(AudioBook).FullName! });
            ctx.BaseItems.Add(new BaseItemEntity { Id = retiredItem.Id, Type = typeof(AudioBook).FullName! });

            // the stale id-key row is inserted first so selection by row order would return it
            ctx.UserData.AddRange(
                CreateUserDataRow(fossilItem, fossilItem.GetUserDataKeys()[1], 111),
                CreateUserDataRow(fossilItem, fossilItem.GetUserDataKeys()[0], 222),
                CreateUserDataRow(retiredItem, "Author-Old Album-0001Old File Name", 333));
            ctx.SaveChanges();
        }

        var result = _userDataManager.GetUserDataBatch([fossilItem, retiredItem], _user);

        Assert.Equal(222, result[fossilItem.Id].PlaybackPositionTicks);
        Assert.Equal(333, result[retiredItem.Id].PlaybackPositionTicks);
    }

    [Fact]
    public void SaveUserData_RowUnderRetiredKey_IsKeptInAgreement()
    {
        var item = CreateAudioBook();

        using (var ctx = CreateDbContext())
        {
            ctx.Users.Add(_user);
            ctx.BaseItems.Add(new BaseItemEntity { Id = item.Id, Type = typeof(AudioBook).FullName! });
            ctx.UserData.Add(CreateUserDataRow(item, "Author-Old Album-0001Old File Name", 111));
            ctx.SaveChanges();
        }

        _userDataManager.SaveUserData(
            _user,
            item,
            new UserItemData { Key = item.GetUserDataKeys()[0], Played = true },
            UserDataSaveReason.UpdateUserRating,
            CancellationToken.None);

        using (var ctx = CreateDbContext())
        {
            var rows = ctx.UserData.Where(e => e.ItemId.Equals(item.Id)).ToList();

            // The retired-key row is what a re-added item reattaches by, so it survives, but it must
            // not keep a playback position that holds the item in Continue Watching.
            Assert.Equal(
                item.GetUserDataKeys().Append("Author-Old Album-0001Old File Name").OrderBy(e => e, StringComparer.Ordinal),
                rows.Select(e => e.CustomDataKey).OrderBy(e => e, StringComparer.Ordinal));
            Assert.All(rows, row =>
            {
                Assert.True(row.Played);
                Assert.Equal(0, row.PlaybackPositionTicks);
            });
        }
    }

    [Fact]
    public void GetUserData_NullUser_ThrowsArgumentNullException()
    {
        var item = CreateAudioBook();
        Assert.Throws<ArgumentNullException>(() => _userDataManager.GetUserData(null!, item));
    }

    [Fact]
    public void RemapPlaybackStreamSelections_MovesEveryUsersSelectionAndKeepsOff()
    {
        var item = CreateAudioBook();
        var otherItem = CreateAudioBook();
        var otherUser = new User("other", "auth-provider", "reset-provider") { Id = Guid.NewGuid() };

        using (var ctx = CreateDbContext())
        {
            ctx.Users.AddRange(_user, otherUser);
            ctx.BaseItems.Add(new BaseItemEntity { Id = item.Id, Type = typeof(AudioBook).FullName! });
            ctx.BaseItems.Add(new BaseItemEntity { Id = otherItem.Id, Type = typeof(AudioBook).FullName! });

            var mine = CreateUserDataRow(item, item.GetUserDataKeys()[0], 0);
            mine.AudioStreamIndex = 2;
            mine.SubtitleStreamIndex = 0;

            var theirs = CreateUserDataRow(item, item.GetUserDataKeys()[0], 0);
            theirs.UserId = otherUser.Id;
            theirs.AudioStreamIndex = 3;
            theirs.SubtitleStreamIndex = -1;

            var unrelated = CreateUserDataRow(otherItem, otherItem.GetUserDataKeys()[0], 0);
            unrelated.AudioStreamIndex = 2;

            ctx.UserData.AddRange(mine, theirs, unrelated);
            ctx.SaveChanges();
        }

        // old layout: external sub 0, video 1, jpn 2, eng 3; new layout: video 0, jpn 1, eng 2, external sub 3
        _userDataManager.RemapPlaybackStreamSelections(item, new Dictionary<int, int?> { [0] = 3, [1] = 0, [2] = 1, [3] = 2 });

        using (var ctx = CreateDbContext())
        {
            var mineAfter = ctx.UserData.Single(e => e.ItemId.Equals(item.Id) && e.UserId.Equals(_user.Id));
            Assert.Equal(1, mineAfter.AudioStreamIndex);
            Assert.Equal(3, mineAfter.SubtitleStreamIndex);

            var theirsAfter = ctx.UserData.Single(e => e.ItemId.Equals(item.Id) && e.UserId.Equals(otherUser.Id));
            Assert.Equal(2, theirsAfter.AudioStreamIndex);
            Assert.Equal(-1, theirsAfter.SubtitleStreamIndex);

            Assert.Equal(2, ctx.UserData.Single(e => e.ItemId.Equals(otherItem.Id)).AudioStreamIndex);
        }
    }

    [Fact]
    public void RemapPlaybackStreamSelections_StreamGone_ClearsSelectionAndUpdatesCache()
    {
        var item = CreateAudioBook();

        using (var ctx = CreateDbContext())
        {
            ctx.Users.Add(_user);
            ctx.BaseItems.Add(new BaseItemEntity { Id = item.Id, Type = typeof(AudioBook).FullName! });
            var row = CreateUserDataRow(item, item.GetUserDataKeys()[0], 0);
            row.AudioStreamIndex = 1;
            row.SubtitleStreamIndex = 4;
            ctx.UserData.Add(row);
            ctx.SaveChanges();
        }

        // The batch read caches the row, so a stale cached copy would show up on the second batch read.
        Assert.Equal(4, _userDataManager.GetUserDataBatch([item], _user)[item.Id].SubtitleStreamIndex);

        _userDataManager.RemapPlaybackStreamSelections(item, new Dictionary<int, int?> { [4] = null });

        var cached = _userDataManager.GetUserDataBatch([item], _user)[item.Id];
        Assert.Equal(1, cached.AudioStreamIndex);
        Assert.Null(cached.SubtitleStreamIndex);

        // The item's preloaded rows are refreshed too, which is what GetUserData reads.
        var preloaded = _userDataManager.GetUserData(_user, item)!;
        Assert.Equal(1, preloaded.AudioStreamIndex);
        Assert.Null(preloaded.SubtitleStreamIndex);
    }
}
