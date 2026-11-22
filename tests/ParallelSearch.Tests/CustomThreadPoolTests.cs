
using System.Threading;
using ParallelSearch.Core;

namespace ParallelSearch.Tests;

public sealed class CustomThreadPoolTests
{
    [Fact]
    public void Enqueue_ExecutesAllTasks()
    {
        using var pool = new CustomThreadPool(4);
        int completedTasks = 0;

        for (int i = 0; i < 20; i++)
        {
            pool.Enqueue(() =>
            {
                Interlocked.Increment(ref completedTasks);
            });
        }

        pool.WaitForAll();

        Assert.Equal(20, completedTasks);
    }

    [Fact]
    public void Enqueue_WithTwoWorkers_RunsTasksConcurrently()
    {
        using var pool = new CustomThreadPool(2);
        using var started = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);

        try
        {
            for (int i = 0; i < 2; i++)
            {
                pool.Enqueue(() =>
                {
                    started.Signal();
                    release.Wait();
                });
            }

            Assert.True(
                started.Wait(TimeSpan.FromSeconds(5)),
                "Обидва завдання повинні почати виконання одночасно.");
        }
        finally
        {
            release.Set();
        }

        pool.WaitForAll();
    }

    [Fact]
    public async Task WaitForAll_WaitsForTaskCompletion()
    {
        using var pool = new CustomThreadPool(2);
        using var taskStarted = new ManualResetEventSlim(false);
        using var releaseTask = new ManualResetEventSlim(false);
        using var waitStarted = new ManualResetEventSlim(false);
        int completed = 0;

        pool.Enqueue(() =>
        {
            taskStarted.Set();
            releaseTask.Wait();
            Interlocked.Exchange(ref completed, 1);
        });

        Assert.True(taskStarted.Wait(TimeSpan.FromSeconds(5)));

        Task waitTask = Task.Run(() =>
        {
            waitStarted.Set();
            pool.WaitForAll();
        });

        Assert.True(waitStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(waitTask.IsCompleted);

        releaseTask.Set();
        await waitTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, completed);
    }

    [Fact]
    public void Dispose_CompletesTasksAndRejectsNewWork()
    {
        var pool = new CustomThreadPool(2);
        int completed = 0;

        pool.Enqueue(() =>
        {
            Interlocked.Increment(ref completed);
        });

        pool.Dispose();

        Assert.Equal(1, completed);

        Assert.Throws<ObjectDisposedException>(() =>
        {
            pool.Enqueue(() => { });
        });
    }

    [Fact]
    public async Task Enqueue_FromMultipleThreads_ExecutesEveryTask()
    {
        using var pool = new CustomThreadPool(4);
        using var startProducers = new ManualResetEventSlim(false);
        const int producerCount = 8;
        const int tasksPerProducer = 100;
        int completedTasks = 0;

        Task[] producers = Enumerable.Range(0, producerCount)
            .Select(_ => Task.Run(() =>
            {
                startProducers.Wait();

                for (int taskIndex = 0; taskIndex < tasksPerProducer; taskIndex++)
                {
                    pool.Enqueue(() => Interlocked.Increment(ref completedTasks));
                }
            }))
            .ToArray();

        startProducers.Set();

        await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(pool.WaitForAll).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(producerCount * tasksPerProducer, completedTasks);
    }

    [Fact]
    public async Task WaitForAll_WhenTaskThrows_ReportsErrorAndCompletesOtherTasks()
    {
        using var pool = new CustomThreadPool(2);
        int completedTasks = 0;

        pool.Enqueue(() => Interlocked.Increment(ref completedTasks));
        pool.Enqueue(() => throw new InvalidOperationException("Task failed."));
        pool.Enqueue(() => Interlocked.Increment(ref completedTasks));

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(
            () => Task.Run(pool.WaitForAll).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, completedTasks);
        InvalidOperationException innerException = Assert.IsType<InvalidOperationException>(
            Assert.Single(exception.InnerExceptions));
        Assert.Equal("Task failed.", innerException.Message);
    }

    [Fact]
    public void WaitForAll_AfterFailure_DoesNotReportPreviousErrorsAgain()
    {
        using var pool = new CustomThreadPool(1);
        int completedTasks = 0;

        pool.Enqueue(() => throw new InvalidOperationException("First task failed."));

        Assert.Throws<AggregateException>(pool.WaitForAll);

        pool.Enqueue(() => Interlocked.Increment(ref completedTasks));

        Exception? secondWaitException = Record.Exception(pool.WaitForAll);

        Assert.Equal(1, completedTasks);
        Assert.Null(secondWaitException);
    }

    [Fact]
    public async Task Dispose_WithUnfinishedTasks_CompletesThemBeforeReturning()
    {
        var pool = new CustomThreadPool(1);
        using var firstTaskStarted = new ManualResetEventSlim(false);
        using var releaseFirstTask = new ManualResetEventSlim(false);
        using var disposeStarted = new ManualResetEventSlim(false);
        int completedTasks = 0;

        pool.Enqueue(() =>
        {
            firstTaskStarted.Set();
            releaseFirstTask.Wait();
            Interlocked.Increment(ref completedTasks);
        });
        pool.Enqueue(() => Interlocked.Increment(ref completedTasks));

        Assert.True(firstTaskStarted.Wait(TimeSpan.FromSeconds(5)));

        Task disposeTask = Task.Run(() =>
        {
            disposeStarted.Set();
            pool.Dispose();
        });

        try
        {
            Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(disposeTask.IsCompleted);
        }
        finally
        {
            releaseFirstTask.Set();
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, completedTasks);
    }

    [Fact]
    public void Dispose_WhenCalledMoreThanOnce_DoesNotThrow()
    {
        var pool = new CustomThreadPool(1);

        pool.Dispose();
        Exception? exception = Record.Exception(pool.Dispose);

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithInvalidWorkerCount_Throws(int workerCount)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new CustomThreadPool(workerCount));

        Assert.Equal("workerCount", exception.ParamName);
    }
}
