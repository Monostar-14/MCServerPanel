using MCServerPanel.Services;

namespace MCServerPanel.Tests;

public class EditSessionRegistryTests
{
    private const string FileA = "DemoPlugin/config.yml";
    private const string FileB = "DemoPlugin/other.yml";

    [Fact]
    public void FirstClientGetsTheLock()
    {
        var reg = new EditSessionRegistry();
        var r = reg.Claim(FileA, "alice", "Alice", takeover: false);

        Assert.True(r.Ok);
        Assert.Null(r.Holder);
        Assert.True(reg.CanSave(FileA, "alice"));
        Assert.Equal("Alice", reg.HolderOf(FileA)!.ClientName);
    }

    [Fact]
    public void SecondClientIsBlockedButCanSeeHolder()
    {
        var reg = new EditSessionRegistry();
        reg.Claim(FileA, "alice", "Alice", takeover: false);

        var r = reg.Claim(FileA, "bob", "Bob", takeover: false);
        Assert.False(r.Ok);
        Assert.Equal("alice", r.Holder!.ClientId);
        Assert.Equal("Alice", r.Holder.ClientName);
        Assert.False(reg.CanSave(FileA, "bob"));
        Assert.True(reg.CanSave(FileA, "alice"));
    }

    [Fact]
    public void TakeoverMovesTheLockAndReportsPreviousHolder()
    {
        var reg = new EditSessionRegistry();
        reg.Claim(FileA, "alice", "Alice", takeover: false);

        var r = reg.Claim(FileA, "bob", "Bob", takeover: true);
        Assert.True(r.Ok);
        Assert.Equal("alice", r.Previous!.ClientId);
        Assert.True(reg.CanSave(FileA, "bob"));
        Assert.False(reg.CanSave(FileA, "alice"));
        Assert.Single(reg.Snapshot("bob"));
        Assert.True(reg.Snapshot("bob")[0].Mine);
    }

    [Fact]
    public void DifferentFilesDoNotConflict()
    {
        var reg = new EditSessionRegistry();
        reg.Claim(FileA, "alice", "Alice", takeover: false);
        var r = reg.Claim(FileB, "bob", "Bob", takeover: false);

        Assert.True(r.Ok);
        Assert.True(reg.CanSave(FileA, "alice"));
        Assert.True(reg.CanSave(FileB, "bob"));
    }

    [Fact]
    public void ReleaseFreesTheLock()
    {
        var reg = new EditSessionRegistry();
        reg.Claim(FileA, "alice", "Alice", takeover: false);
        reg.Release("alice");

        Assert.True(reg.CanSave(FileA, "bob"));
        Assert.Empty(reg.Snapshot(null));
    }

    [Fact]
    public void StaleLockExpiresSoACrashedTabCannotBlockForever()
    {
        var reg = new EditSessionRegistry(TimeSpan.FromMilliseconds(60));
        reg.Claim(FileA, "alice", "Alice", takeover: false);
        Assert.False(reg.CanSave(FileA, "bob"));

        Thread.Sleep(120);

        Assert.True(reg.CanSave(FileA, "bob"));
        Assert.Empty(reg.Snapshot(null));
    }

    [Fact]
    public void RenewKeepsTheLockAlive()
    {
        var reg = new EditSessionRegistry(TimeSpan.FromMilliseconds(150));
        reg.Claim(FileA, "alice", "Alice", takeover: false);

        for (int i = 0; i < 3; i++)
        {
            Thread.Sleep(60);
            reg.Renew("alice");
        }

        Assert.False(reg.CanSave(FileA, "bob"));
        Assert.True(reg.CanSave(FileA, "alice"));
    }

    [Fact]
    public void SnapshotMarksOwnLockAndOtherFiles()
    {
        var reg = new EditSessionRegistry();
        reg.Claim(FileA, "alice", "Alice", takeover: false);
        reg.Claim(FileB, "bob", "Bob", takeover: false);

        var mine = reg.Snapshot("bob");
        Assert.Equal(2, mine.Count);
        Assert.True(mine.Single(l => l.File == FileB).Mine);
        Assert.False(mine.Single(l => l.File == FileA).Mine);
    }
}
