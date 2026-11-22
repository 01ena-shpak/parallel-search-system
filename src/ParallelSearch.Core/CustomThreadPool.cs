
using System.Threading;

namespace ParallelSearch.Core;

public sealed class CustomThreadPool : IDisposable
{
    private readonly object _sync = new();
    private readonly Thread[] _workers;
    private readonly List<Exception> _errors = [];

    private WorkItem? _first;
    private WorkItem? _last;

    private int _unfinishedTasks;
    private bool _stopping;

    public CustomThreadPool(int workerCount)
    {
        if (workerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount));
        }

        _workers = new Thread[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            _workers[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"ParallelSearch.Worker-{i + 1}"
            };

            _workers[i].Start();
        }
    }

    public void Enqueue(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_sync)
        {
            if (_stopping)
            {
                throw new ObjectDisposedException(
                    nameof(CustomThreadPool));
            }

            var item = new WorkItem(work);

            if (_last is null)
            {
                _first = item;
                _last = item;
            }
            else
            {
                _last.Next = item;
                _last = item;
            }

            _unfinishedTasks++;

            Monitor.Pulse(_sync);
        }
    }

    public void WaitForAll()
    {
        EnsureNotWorkerThread();

        Exception[] errors;

        lock (_sync)
        {
            while (_unfinishedTasks > 0)
            {
                Monitor.Wait(_sync);
            }

            errors = _errors.ToArray();
            _errors.Clear();
        }

        if (errors.Length > 0)
        {
            throw new AggregateException(
                "One or more thread pool tasks failed.",
                errors);
        }
    }

    public void Dispose()
    {
        EnsureNotWorkerThread();

        lock (_sync)
        {
            _stopping = true;
            Monitor.PulseAll(_sync);
        }

        foreach (Thread worker in _workers)
        {
            worker.Join();
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            Action work;

            lock (_sync)
            {
                while (_first is null && !_stopping)
                {
                    Monitor.Wait(_sync);
                }

                if (_first is null && _stopping)
                {
                    return;
                }

                WorkItem item = _first!;

                _first = item.Next;

                if (_first is null)
                {
                    _last = null;
                }

                work = item.Work;
            }

            try
            {
                work();
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _errors.Add(exception);
                }
            }
            finally
            {
                lock (_sync)
                {
                    _unfinishedTasks--;

                    if (_unfinishedTasks == 0)
                    {
                        Monitor.PulseAll(_sync);
                    }
                }
            }
        }
    }

    private void EnsureNotWorkerThread()
    {
        foreach (Thread worker in _workers)
        {
            if (Thread.CurrentThread == worker)
            {
                throw new InvalidOperationException(
                    "A worker cannot wait for or dispose its own pool.");
            }
        }
    }

    private sealed class WorkItem
    {
        public WorkItem(Action work)
        {
            Work = work;
        }

        public Action Work { get; }

        public WorkItem? Next { get; set; }
    }
}
