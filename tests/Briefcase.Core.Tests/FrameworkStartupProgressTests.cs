using Briefcase.Rendering;

namespace Briefcase.Core.Tests;

public sealed class FrameworkStartupProgressTests
{
    [Fact]
    public void Reports_are_monotonic_and_completion_is_terminal()
    {
        var progress = new FrameworkStartupProgress();
        var snapshots = new List<FrameworkStartupSnapshot>();
        progress.Changed += snapshots.Add;

        progress.Report(0.4, "Mods", "First");
        progress.Report(0.2, "Mods", "Late report");
        progress.Complete();
        progress.Report(0.9, "Ignored");

        Assert.Equal(3, snapshots.Count);
        Assert.Equal(0.4, snapshots[0].Progress);
        Assert.Equal(0.4, snapshots[1].Progress);
        Assert.True(snapshots[2].IsComplete);
        Assert.Equal(1, progress.Snapshot.Progress);
        Assert.Equal("Briefcase is ready", progress.Snapshot.Stage);
    }

    [Fact]
    public void Failure_preserves_a_user_visible_reason()
    {
        var progress = new FrameworkStartupProgress();

        progress.Fail("The SDK could not be loaded.");

        Assert.True(progress.Snapshot.IsComplete);
        Assert.True(progress.Snapshot.HasFailed);
        Assert.Equal("The SDK could not be loaded.", progress.Snapshot.Detail);
    }
}
