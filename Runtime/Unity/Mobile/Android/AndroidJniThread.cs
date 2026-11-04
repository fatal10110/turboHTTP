using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace TurboHTTP.Unity.Mobile.Android
{
    /// <summary>
    /// The JNI thread-attachment calls <see cref="AndroidJniThread"/> makes: Unity's
    /// <c>AndroidJNI</c> in Android players (<see cref="UnityAndroidJni"/>), fakes in tests.
    /// </summary>
    internal interface IAndroidJniAttachment
    {
        /// <summary>Attaches the calling thread to the Java VM; 0 on success.</summary>
        int AttachCurrentThread();

        /// <summary>Detaches the calling thread from the Java VM; 0 on success.</summary>
        int DetachCurrentThread();

        /// <summary>Opens a local-reference frame on the calling thread; 0 on success.</summary>
        int PushLocalFrame(int capacity);

        /// <summary>Releases every local reference created since the matching <see cref="PushLocalFrame"/>.</summary>
        void PopLocalFrame();

        /// <summary>Clears a pending Java exception, if any.</summary>
        void ExceptionClear();
    }

    /// <summary>
    /// Runs TurboHTTP's JNI calls on one long-lived thread that attaches to the Java VM once and
    /// detaches when it stops, so no thread-pool thread is ever attached by TurboHTTP (#328).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thread attached with <c>AttachCurrentThread</c> must call <c>DetachCurrentThread</c> before
    /// it exits; Android 6 (API 23) aborts the process otherwise, and a recycled pool thread exits
    /// whenever the pool shrinks. Unity's <c>AndroidJNI</c> has no <c>GetEnv</c>, so a caller cannot
    /// tell whether a thread was attached before it, and detaching one that someone else attached
    /// (the Unity main thread, a Java thread calling into C#) would break that owner. Callers that
    /// are known to stay attached for the life of the process (the Unity main thread) therefore run
    /// the work inline; every other caller blocks until this thread has run it.
    /// </para>
    /// <para>
    /// Each work item runs inside its own local-reference frame and ends with a cleared Java
    /// exception, because a native-attached thread that never returns to Java would otherwise keep
    /// every leaked local reference. <see cref="Invoke"/> is synchronous by design: the callers
    /// (<see cref="AndroidCleartextTrafficPolicy"/>, the background plugin) are synchronous APIs.
    /// The work must not wait on another <see cref="Invoke"/> from a different thread.
    /// </para>
    /// </remarks>
    internal sealed class AndroidJniThread : IDisposable
    {
        private const int LocalFrameCapacity = 32;

        /// <summary>
        /// The process-wide JNI thread. The Unity main thread runs work inline. The thread starts on
        /// the first marshalled call and is never stopped in a player.
        /// </summary>
        internal static readonly AndroidJniThread Shared =
            new AndroidJniThread(UnityAndroidJni.Instance, MainThreadDispatcher.IsMainThread);

        private readonly IAndroidJniAttachment _jni;
        private readonly Func<bool> _isPermanentlyAttachedCaller;

        // _queue, _thread and _disposed are owned by _gate. Callers wait on _gate for their item to
        // complete; the JNI thread waits on it for work.
        private readonly object _gate = new object();
        private readonly Queue<WorkItem> _queue = new Queue<WorkItem>();
        private Thread _thread;
        private bool _disposed;

        /// <param name="jni">The attachment calls.</param>
        /// <param name="isPermanentlyAttachedCaller">
        /// <see langword="true"/> when the calling thread stays attached for the life of the process
        /// and may run JNI inline; may be <see langword="null"/> (every caller is marshalled).
        /// </param>
        internal AndroidJniThread(IAndroidJniAttachment jni, Func<bool> isPermanentlyAttachedCaller)
        {
            _jni = jni ?? throw new ArgumentNullException(nameof(jni));
            _isPermanentlyAttachedCaller = isPermanentlyAttachedCaller;
        }

        /// <summary>
        /// Runs <paramref name="work"/> on an attached thread and returns its result. An exception
        /// from the work is rethrown to the caller; failing to attach throws
        /// <see cref="InvalidOperationException"/> (the next call tries again).
        /// </summary>
        internal bool Invoke(Func<bool> work)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));

            if (_isPermanentlyAttachedCaller != null && _isPermanentlyAttachedCaller())
                return work();

            var item = new WorkItem(work);
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(AndroidJniThread));

                // Work that calls back into Invoke from the JNI thread itself is already attached.
                if (_thread == Thread.CurrentThread)
                {
                    item = null;
                }
                else
                {
                    _queue.Enqueue(item);
                    if (_thread == null)
                    {
                        _thread = new Thread(Run) { IsBackground = true, Name = "TurboHTTP JNI" };
                        _thread.Start();
                    }
                    else
                    {
                        Monitor.PulseAll(_gate);
                    }

                    while (!item.Done)
                        Monitor.Wait(_gate);
                }
            }

            if (item == null)
                return work();

            item.Error?.Throw();
            return item.Result;
        }

        /// <summary>
        /// Stops the JNI thread after the queued work has run and waits for it to detach. Later
        /// <see cref="Invoke"/> calls throw <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            Thread thread;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                thread = _thread;
                Monitor.PulseAll(_gate);
            }

            if (thread != null && thread != Thread.CurrentThread)
                thread.Join();
        }

        private void Run()
        {
            if (_jni.AttachCurrentThread() != 0)
            {
                // Not attached, so nothing to detach. Fail what is queued; the next call starts a
                // new thread and tries again.
                ExitAndFailQueued(new InvalidOperationException(
                    "TurboHTTP could not attach its JNI thread to the Java VM."));
                return;
            }

            try
            {
                while (true)
                {
                    WorkItem item;
                    lock (_gate)
                    {
                        while (_queue.Count == 0 && !_disposed)
                            Monitor.Wait(_gate);

                        if (_queue.Count == 0)
                        {
                            _thread = null;
                            return;
                        }

                        item = _queue.Dequeue();
                    }

                    Execute(item);

                    lock (_gate)
                    {
                        item.Done = true;
                        Monitor.PulseAll(_gate);
                    }
                }
            }
            catch (Exception e)
            {
                // Execute catches the work's exceptions; this only guards the loop itself so that no
                // caller waits forever.
                ExitAndFailQueued(e);
            }
            finally
            {
                _jni.DetachCurrentThread();
            }
        }

        // Never throws: the work's exception, or a failing JNI call, is handed to the caller.
        private void Execute(WorkItem item)
        {
            try
            {
                if (_jni.PushLocalFrame(LocalFrameCapacity) != 0)
                {
                    _jni.ExceptionClear();
                    throw new InvalidOperationException("TurboHTTP could not reserve JNI local references.");
                }

                try
                {
                    item.Result = item.Work();
                }
                finally
                {
                    _jni.ExceptionClear();
                    _jni.PopLocalFrame();
                }
            }
            catch (Exception e)
            {
                item.Error = ExceptionDispatchInfo.Capture(e);
            }
        }

        private void ExitAndFailQueued(Exception error)
        {
            lock (_gate)
            {
                if (_thread == Thread.CurrentThread)
                    _thread = null;

                while (_queue.Count > 0)
                {
                    var item = _queue.Dequeue();
                    item.Error = ExceptionDispatchInfo.Capture(error);
                    item.Done = true;
                }

                Monitor.PulseAll(_gate);
            }
        }

        private sealed class WorkItem
        {
            internal readonly Func<bool> Work;

            // Written by the JNI thread before Done is set under _gate; read by the caller after it
            // observed Done under _gate.
            internal bool Result;
            internal ExceptionDispatchInfo Error;
            internal bool Done;

            internal WorkItem(Func<bool> work)
            {
                Work = work;
            }
        }
    }

    /// <summary>
    /// <see cref="IAndroidJniAttachment"/> over Unity's <c>AndroidJNI</c>. Outside Android players
    /// there is no Java VM: attaching fails, so <see cref="AndroidJniThread.Invoke"/> throws.
    /// </summary>
    internal sealed class UnityAndroidJni : IAndroidJniAttachment
    {
        internal static readonly UnityAndroidJni Instance = new UnityAndroidJni();

        private UnityAndroidJni()
        {
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        public int AttachCurrentThread() => UnityEngine.AndroidJNI.AttachCurrentThread();

        public int DetachCurrentThread() => UnityEngine.AndroidJNI.DetachCurrentThread();

        public int PushLocalFrame(int capacity) => UnityEngine.AndroidJNI.PushLocalFrame(capacity);

        public void PopLocalFrame() => UnityEngine.AndroidJNI.PopLocalFrame(IntPtr.Zero);

        public void ExceptionClear() => UnityEngine.AndroidJNI.ExceptionClear();
#else
        public int AttachCurrentThread() => -1;

        public int DetachCurrentThread() => 0;

        public int PushLocalFrame(int capacity) => -1;

        public void PopLocalFrame()
        {
        }

        public void ExceptionClear()
        {
        }
#endif
    }
}
