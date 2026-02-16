using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Dispatches work onto Unity's main thread.
    /// </summary>
    /// <remarks>
    /// In Unity Editor, domain reload can interrupt queued work. Pending work items
    /// are failed deterministically and should be treated as non-recoverable.
    /// </remarks>
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        private enum DispatcherState
        {
            Uninitialized = 0,
            Initializing = 1,
            Ready = 2,
            Disposing = 3
        }

        private interface IDispatchWorkItem
        {
            void Execute();
            void Fail(Exception exception);
        }

        private sealed class DispatchWorkItem : IDispatchWorkItem
        {
            private readonly Action _execute;
            private readonly Action<Exception> _fail;

            public DispatchWorkItem(Action execute, Action<Exception> fail)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _fail = fail ?? throw new ArgumentNullException(nameof(fail));
            }

            public void Execute()
            {
                _execute();
            }

            public void Fail(Exception exception)
            {
                _fail(exception);
            }
        }

        private static readonly object LifecycleLock = new object();
        private static readonly ConcurrentQueue<IDispatchWorkItem> PendingWork =
            new ConcurrentQueue<IDispatchWorkItem>();

        private static MainThreadDispatcher _instance;
        private static int _mainThreadManagedId;
        private static int _state = (int)DispatcherState.Uninitialized;

        private static DispatcherState State =>
            (DispatcherState)Volatile.Read(ref _state);

        /// <summary>
        /// Returns the singleton dispatcher instance.
        /// </summary>
        public static MainThreadDispatcher Instance => EnsureInstanceReady();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Volatile.Write(ref _state, (int)DispatcherState.Uninitialized);
            Volatile.Write(ref _mainThreadManagedId, 0);
            _instance = null;
            FailPendingWork(new OperationCanceledException(
                "MainThreadDispatcher static state reset during domain reload."));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BootstrapBeforeSceneLoad()
        {
            if (!Application.isPlaying)
                return;

            CaptureMainThreadIdIfNeeded();
            EnsureInstanceReady();
        }

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void RegisterEditorReloadHooks()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;

            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
        }
#endif

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            CaptureMainThreadIdIfNeeded();
            Volatile.Write(ref _state, (int)DispatcherState.Ready);
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if (!ReferenceEquals(_instance, this))
                return;

            while (PendingWork.TryDequeue(out var workItem))
            {
                workItem.Execute();
            }
        }

        private void OnApplicationQuit()
        {
            if (!ReferenceEquals(_instance, this))
                return;

            BeginShutdown("MainThreadDispatcher shutting down during application quit.");
        }

        private void OnDestroy()
        {
            if (!ReferenceEquals(_instance, this))
                return;

            BeginShutdown("MainThreadDispatcher was destroyed; pending work was canceled.");
        }

        /// <summary>
        /// Enqueues fire-and-forget work on the main thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            EnqueueWorkItem(new DispatchWorkItem(
                execute: () =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                },
                fail: _ => { }));
        }

        /// <summary>
        /// Executes work on the main thread and returns a completion task.
        /// </summary>
        public static Task ExecuteAsync(
            Action action,
            CancellationToken cancellationToken = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            EnsureInitializedIfMainThread();

            if (State == DispatcherState.Disposing)
                return Task.FromException(new InvalidOperationException(CreateUnavailableMessage(State)));

            if (IsMainThread())
            {
                try
                {
                    action();
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    return Task.FromException(ex);
                }
            }

            var tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var registration = RegisterCancellation(cancellationToken, tcs);

            EnqueueWorkItem(new DispatchWorkItem(
                execute: () =>
                {
                    if (tcs.Task.IsCompleted)
                    {
                        registration.Dispose();
                        return;
                    }

                    try
                    {
                        action();
                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        registration.Dispose();
                    }
                },
                fail: exception =>
                {
                    registration.Dispose();
                    tcs.TrySetException(exception);
                }));

            return tcs.Task;
        }

        /// <summary>
        /// Executes value-producing work on the main thread.
        /// </summary>
        public static Task<T> ExecuteAsync<T>(
            Func<T> func,
            CancellationToken cancellationToken = default)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<T>(cancellationToken);

            EnsureInitializedIfMainThread();

            if (State == DispatcherState.Disposing)
                return Task.FromException<T>(new InvalidOperationException(CreateUnavailableMessage(State)));

            if (IsMainThread())
            {
                try
                {
                    return Task.FromResult(func());
                }
                catch (Exception ex)
                {
                    return Task.FromException<T>(ex);
                }
            }

            var tcs = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var registration = RegisterCancellation(cancellationToken, tcs);

            EnqueueWorkItem(new DispatchWorkItem(
                execute: () =>
                {
                    if (tcs.Task.IsCompleted)
                    {
                        registration.Dispose();
                        return;
                    }

                    try
                    {
                        tcs.TrySetResult(func());
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        registration.Dispose();
                    }
                },
                fail: exception =>
                {
                    registration.Dispose();
                    tcs.TrySetException(exception);
                }));

            return tcs.Task;
        }

        /// <summary>
        /// Synchronously executes work on the main thread.
        /// </summary>
        public static void Execute(Action action)
        {
            ExecuteAsync(action).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronously executes value-producing work on the main thread.
        /// </summary>
        public static T Execute<T>(Func<T> func)
        {
            return ExecuteAsync(func).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns true when called from the captured Unity main thread.
        /// </summary>
        public static bool IsMainThread()
        {
            var captured = Volatile.Read(ref _mainThreadManagedId);
            if (captured <= 0)
                return false;

            return Thread.CurrentThread.ManagedThreadId == captured;
        }

        private static void EnqueueWorkItem(IDispatchWorkItem workItem)
        {
            if (workItem == null) throw new ArgumentNullException(nameof(workItem));

            lock (LifecycleLock)
            {
                EnsureDispatcherAvailableForEnqueue();
                PendingWork.Enqueue(workItem);
            }
        }

        private static MainThreadDispatcher EnsureInstanceReady()
        {
            var instance = _instance;
            if (instance != null && State == DispatcherState.Ready)
                return instance;

            lock (LifecycleLock)
            {
                if (_instance != null && State == DispatcherState.Ready)
                    return _instance;

                if (State == DispatcherState.Disposing)
                    throw new InvalidOperationException(CreateUnavailableMessage(State));

                CaptureMainThreadIdIfNeeded();

                if (!IsMainThread())
                {
                    throw new InvalidOperationException(
                        "MainThreadDispatcher is not initialized yet and cannot be created " +
                        "from a worker thread. Access MainThreadDispatcher.Instance from the " +
                        "Unity main thread during startup before dispatching background work.");
                }

                Volatile.Write(ref _state, (int)DispatcherState.Initializing);

                var go = new GameObject("[TurboHTTP MainThreadDispatcher]");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<MainThreadDispatcher>();

                Volatile.Write(ref _state, (int)DispatcherState.Ready);
                return _instance;
            }
        }

        private static void EnsureDispatcherAvailableForEnqueue()
        {
            var currentState = State;
            if (currentState == DispatcherState.Ready)
                return;

            if (currentState == DispatcherState.Uninitialized ||
                currentState == DispatcherState.Initializing)
            {
                EnsureInstanceReady();
                currentState = State;
                if (currentState == DispatcherState.Ready)
                    return;
            }

            throw new InvalidOperationException(CreateUnavailableMessage(currentState));
        }

        private static void CaptureMainThreadIdIfNeeded()
        {
            if (Volatile.Read(ref _mainThreadManagedId) > 0)
                return;

            var currentManagedId = Thread.CurrentThread.ManagedThreadId;

            if (Thread.CurrentThread.IsThreadPoolThread)
            {
                throw new InvalidOperationException(
                    "MainThreadDispatcher main thread has not been initialized. " +
                    "Initialize it from Unity's main thread first by touching " +
                    "MainThreadDispatcher.Instance during startup.");
            }

            Interlocked.CompareExchange(ref _mainThreadManagedId, currentManagedId, 0);
        }

        private static CancellationTokenRegistration RegisterCancellation<T>(
            CancellationToken cancellationToken,
            TaskCompletionSource<T> tcs)
        {
            if (!cancellationToken.CanBeCanceled)
                return default;

            return cancellationToken.Register(() =>
            {
                tcs.TrySetCanceled(cancellationToken);
            });
        }

        private static void EnsureInitializedIfMainThread()
        {
            if (State != DispatcherState.Uninitialized)
                return;

            if (Volatile.Read(ref _mainThreadManagedId) == 0 && !Thread.CurrentThread.IsThreadPoolThread)
                CaptureMainThreadIdIfNeeded();

            if (IsMainThread())
                EnsureInstanceReady();
        }

        private static void BeginShutdown(string reason)
        {
            lock (LifecycleLock)
            {
                if (State == DispatcherState.Disposing)
                    return;

                Volatile.Write(ref _state, (int)DispatcherState.Disposing);

                var failure = new OperationCanceledException(reason);
                FailPendingWork(failure);

                _instance = null;
            }
        }

        private static void FailPendingWork(Exception exception)
        {
            while (PendingWork.TryDequeue(out var workItem))
            {
                try
                {
                    workItem.Fail(exception);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        private static string CreateUnavailableMessage(DispatcherState state)
        {
            switch (state)
            {
                case DispatcherState.Disposing:
                    return "MainThreadDispatcher is disposing or reloading. New work is rejected.";
                case DispatcherState.Initializing:
                    return "MainThreadDispatcher is still initializing. Retry after initialization completes.";
                default:
                    return "MainThreadDispatcher is unavailable. Ensure it is initialized on the Unity main thread.";
            }
        }

#if UNITY_EDITOR
        private static void HandleBeforeAssemblyReload()
        {
            BeginShutdown(
                "MainThreadDispatcher canceled queued work because the Unity editor domain is reloading.");
        }

        private static void HandlePlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                BeginShutdown(
                    "MainThreadDispatcher canceled queued work because Play Mode is exiting.");
            }
        }
#endif
    }
}
