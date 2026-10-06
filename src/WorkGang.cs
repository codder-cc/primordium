using System;
using System.Threading;

namespace Primordium;

// A few dedicated threads for the view's parallel loops. The thread pool is busy with the simulation's
// agent phase most of the time; work queued there would wait behind it and stall the frame. The
// calling thread takes part, so a loop never waits for a worker that has not woken up yet.
public sealed class WorkGang
{
    readonly Thread[] threads;
    readonly SemaphoreSlim start = new(0);
    readonly ManualResetEventSlim done = new(false);
    Action<int> body;
    int count, next = int.MaxValue / 2, remaining;
    Exception error;
    volatile bool stop;

    public WorkGang(int workers)
    {
        threads = new Thread[workers];
        for (int k = 0; k < workers; k++)
        {
            threads[k] = new Thread(Loop) { Name = "view worker", IsBackground = true };
            threads[k].Start();
        }
    }

    // body(0) … body(n − 1), spread over the workers and the caller; returns when all are done.
    public void For(int n, Action<int> work)
    {
        if (n <= 1 || threads.Length == 0)
        {
            for (int i = 0; i < n; i++) work(i);
            return;
        }
        body = work; count = n; remaining = n; error = null;
        done.Reset();
        Volatile.Write(ref next, 0);
        start.Release(Math.Min(threads.Length, n - 1));
        Work();
        done.Wait();
        Volatile.Write(ref next, int.MaxValue / 2);
        if (error != null) throw new AggregateException(error);
    }

    void Work()
    {
        int i;
        while ((i = Interlocked.Increment(ref next) - 1) < count)
        {
            try { body(i); }
            catch (Exception e) { error ??= e; }
            if (Interlocked.Decrement(ref remaining) == 0) done.Set();
        }
    }

    void Loop()
    {
        while (true)
        {
            start.Wait();
            if (stop) return;
            Work();
        }
    }

    public void Stop()
    {
        stop = true;
        start.Release(threads.Length);
    }
}
