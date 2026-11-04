using System;
using System.Threading.Tasks;

/// <summary>
/// Runs an asynchronous test body from a synchronous <c>[Test]</c> (Unity's NUnit has no
/// <c>async Task</c> tests). The body runs on the thread pool and the calling thread, in
/// PlayMode the Unity main thread, waits for it with a bound: a body that never completes fails
/// its test with <see cref="AssertAsyncTimeoutException"/> after <see cref="DefaultBound"/>
/// instead of freezing the player loop and ending the whole run (#330). The abandoned body keeps
/// running on the pool.
/// </summary>
public static class AssertAsync
{
    /// <summary>
    /// Longest a body may run. Well above every legitimate body (the slowest bound one, 30 s, plus
    /// handshakes on a contended emulator); a test that needs longer passes its own bound.
    /// </summary>
    public static readonly TimeSpan DefaultBound = TimeSpan.FromMinutes(5);

    public static void Run(Func<Task> asyncDelegate)
    {
        Run(asyncDelegate, DefaultBound);
    }

    /// <summary>Runs <paramref name="asyncDelegate"/>; fails when it does not complete within <paramref name="bound"/>.</summary>
    public static void Run(Func<Task> asyncDelegate, TimeSpan bound)
    {
        if (asyncDelegate == null)
            throw new ArgumentNullException(nameof(asyncDelegate));

        var task = Task.Run(asyncDelegate);
        WaitBounded(task, bound);
        task.GetAwaiter().GetResult();
    }

    public static void Run(Func<ValueTask> asyncDelegate, bool preferValueTask = true)
    {
        if (asyncDelegate == null)
            throw new ArgumentNullException(nameof(asyncDelegate));

        _ = preferValueTask;
        var task = Task.Run(async () => await asyncDelegate().ConfigureAwait(false));
        WaitBounded(task, DefaultBound);
        task.GetAwaiter().GetResult();
    }

    public static T ThrowsAsync<T>(Func<Task> asyncDelegate) where T : Exception
    {
        return ThrowsAsync<T>(asyncDelegate, DefaultBound);
    }

    /// <summary>
    /// Expects <typeparamref name="T"/> (exact type); fails when the body does not complete within
    /// <paramref name="bound"/>. The bound failure is never matched against <typeparamref name="T"/>.
    /// </summary>
    public static T ThrowsAsync<T>(Func<Task> asyncDelegate, TimeSpan bound) where T : Exception
    {
        if (asyncDelegate == null)
            throw new ArgumentNullException(nameof(asyncDelegate));

        var task = Task.Run(asyncDelegate);
        WaitBounded(task, bound);
        return Expect<T>(task);
    }

    public static T ThrowsAsync<T>(Func<ValueTask> asyncDelegate, bool preferValueTask = true) where T : Exception
    {
        if (asyncDelegate == null)
            throw new ArgumentNullException(nameof(asyncDelegate));

        _ = preferValueTask;
        var task = Task.Run(async () => await asyncDelegate().ConfigureAwait(false));
        WaitBounded(task, DefaultBound);
        return Expect<T>(task);
    }

    public static T ThrowsAsync<T, TResult>(Func<ValueTask<TResult>> asyncDelegate) where T : Exception
    {
        if (asyncDelegate == null)
            throw new ArgumentNullException(nameof(asyncDelegate));

        Task task = Task.Run(async () => await asyncDelegate().ConfigureAwait(false));
        WaitBounded(task, DefaultBound);
        return Expect<T>(task);
    }

    public static T ThrowsAsync<T, TResult>(Func<Task<TResult>> asyncDelegate) where T : Exception
    {
        if (asyncDelegate == null)
            throw new ArgumentNullException(nameof(asyncDelegate));

        Task task = Task.Run(asyncDelegate);
        WaitBounded(task, DefaultBound);
        return Expect<T>(task);
    }

    // Outside every catch (T): a hang fails the test even for ThrowsAsync<Exception>.
    private static void WaitBounded(Task task, TimeSpan bound)
    {
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(bound))
        {
            throw new AssertAsyncTimeoutException(
                "The asynchronous test body did not complete within " + bound.TotalSeconds +
                " s; the test fails so the run can go on (#330). The body keeps running on the thread pool.");
        }
    }

    private static T Expect<T>(Task completed) where T : Exception
    {
        try
        {
            completed.GetAwaiter().GetResult();
        }
        catch (T expected)
        {
            return expected;
        }
        catch (Exception ex)
        {
            throw new Exception($"Expected exception of type {typeof(T).Name}, but got {ex.GetType().Name}.", ex);
        }

        throw new Exception($"Expected exception of type {typeof(T).Name}, but no exception was thrown.");
    }
}

/// <summary>An <see cref="AssertAsync"/> body did not complete within its bound (#330).</summary>
public sealed class AssertAsyncTimeoutException : Exception
{
    public AssertAsyncTimeoutException(string message)
        : base(message)
    {
    }
}
