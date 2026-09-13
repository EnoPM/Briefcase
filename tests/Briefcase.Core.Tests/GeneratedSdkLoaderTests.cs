using Briefcase.ManagedHost;

namespace Briefcase.Core.Tests;

public sealed class GeneratedSdkLoaderTests
{
    [Fact]
    public void Waits_for_snapshot_published_by_native_probe()
    {
        var scans = 0;
        var retries = new List<int>();

        var available = GeneratedSdkLoader.WaitForAvailable<string>(
            () => ++scans < 3 ? [] : ["client-snapshot"],
            maxRetries: 5,
            retry: retries.Add);

        Assert.Equal(["client-snapshot"], available);
        Assert.Equal([1, 2], retries);
        Assert.Equal(3, scans);
    }

    [Fact]
    public void Stops_after_retry_budget_when_snapshot_is_absent()
    {
        var scans = 0;
        var retries = 0;

        var available = GeneratedSdkLoader.WaitForAvailable<string>(
            () =>
            {
                scans++;
                return [];
            },
            maxRetries: 2,
            retry: _ => retries++);

        Assert.Empty(available);
        Assert.Equal(3, scans);
        Assert.Equal(2, retries);
    }

    [Fact]
    public void Returns_ambiguous_snapshot_set_without_retrying()
    {
        var retries = 0;

        var available = GeneratedSdkLoader.WaitForAvailable<string>(
            () => new[] { "client", "server" },
            maxRetries: 5,
            retry: _ => retries++);

        Assert.Equal(["client", "server"], available);
        Assert.Equal(0, retries);
    }
}
