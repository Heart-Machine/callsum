using Callsum.App;

namespace Callsum.App.Tests;

public sealed class UpdatesTests
{
    [Fact]
    public async Task CheckAsync_joins_the_check_already_in_progress()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<Updates.Check>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var updates = new Updates(async () =>
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            return await finish.Task;
        });

        var automatic = updates.CheckAsync();
        await started.Task;
        var manual = updates.CheckAsync();

        Assert.Same(automatic, manual);
        finish.SetResult(new Updates.Check("1.1.0", null));

        var check = await manual;
        Assert.Equal(1, calls);
        Assert.Equal("1.1.0", check.Version);
    }
}
