using FleetWise.Services;
using Microsoft.Extensions.Caching.Memory;

namespace RouteSyncWeb.Tests;

public class SharedReadTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Viewers_asking_at_the_same_time_share_one_read()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var reads = 0;
        var gate = new TaskCompletionSource<int>();
        Task<int> Read() { reads++; return gate.Task; }

        var first = SharedRead.GetAsync(cache, "k", Minute, Read);
        var second = SharedRead.GetAsync(cache, "k", Minute, Read);
        gate.SetResult(7);

        Assert.Equal(7, await first);
        Assert.Equal(7, await second);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task A_failed_read_is_not_handed_to_the_next_viewer()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var reads = 0;
        Task<int> Read() => ++reads == 1 ? throw new InvalidOperationException("down") : Task.FromResult(reads);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SharedRead.GetAsync(cache, "k", Minute, Read));

        Assert.Equal(2, await SharedRead.GetAsync(cache, "k", Minute, Read));
    }

    [Fact]
    public async Task A_fresh_read_replaces_the_shared_answer()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var reads = 0;
        Task<int> Read() => Task.FromResult(++reads);

        Assert.Equal(1, await SharedRead.GetAsync(cache, "k", Minute, Read));
        Assert.Equal(1, await SharedRead.GetAsync(cache, "k", Minute, Read));
        Assert.Equal(2, await SharedRead.GetAsync(cache, "k", Minute, Read, fresh: true));
        Assert.Equal(2, await SharedRead.GetAsync(cache, "k", Minute, Read));
    }

    [Fact]
    public async Task Each_key_is_read_on_its_own()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());

        Assert.Equal("all", await SharedRead.GetAsync(cache, "dashboard:all", Minute, () => Task.FromResult("all")));
        Assert.Equal("route 1", await SharedRead.GetAsync(cache, "dashboard:1", Minute, () => Task.FromResult("route 1")));
    }

    [Fact]
    public async Task An_answer_is_read_again_once_it_expires()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var reads = 0;
        Task<int> Read() => Task.FromResult(++reads);

        Assert.Equal(1, await SharedRead.GetAsync(cache, "k", TimeSpan.FromMilliseconds(40), Read));
        await Task.Delay(120);

        Assert.Equal(2, await SharedRead.GetAsync(cache, "k", TimeSpan.FromMilliseconds(40), Read));
    }
}
