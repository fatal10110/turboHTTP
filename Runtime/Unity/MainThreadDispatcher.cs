using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.LowLevel;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Snapshot metrics for the dispatcher runtime.
    /// </summary>
    public readonly struct MainThreadDispatcherMetrics
    {
        public MainThreadDispatcherMetrics(
            MainThreadDispatcherLifecycleState lifecycleState,
            int mainThreadManagedId,
            bool hasUnitySynchronizationContext,
            long executedUserItems,
            long executedControlItems,
            long dispatchExceptions,
            double averageQueueLatencyMs,
            MainThreadWorkQueueMetrics queue)
        {
            LifecycleState = lifecycleState;
            MainThreadManagedId = mainThreadManagedId;
            HasUnitySynchronizationContext = hasUnitySynchronizationContext;
            ExecutedUserItems = executedUserItems;
            ExecutedControlItems = executedControlItems;
            DispatchExceptions = dispatchExceptions;
            AverageQueueLatencyMs = averageQueueLatencyMs;
            Queue = queue;
        }

        public MainThreadDispatcherLifecycleState LifecycleState { get; }
        public int MainThreadManagedId { get; }
        public bool HasUnitySynchronizationContext { get; }
        public long ExecutedUserItems { get; }
        public long ExecutedControlItems { get; }
        public long DispatchExceptions { get; }
        public double AverageQueueLatencyMs { get; }
        public MainThreadWorkQueueMetrics Queue { get; }
    }

    /// <summary>
    /// Dispatches work onto Unity's main thread.
    /// </summary>
    /// <remarks>
    /// In Unity Editor, domain reload can interrupt queued work. Pending work items
    /// are failed deterministically and should be treated as non-recoverable.
    /// </remarks>
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        private sealed class DispatchWorkItem : IMainThreadDispatchWorkItem
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

        private sealed class PlayerLoopDispatchMarker
        {
        }

        private static readonly object LifecycleLock = new object();

        private static MainThreadDispatcher _instance;
        private static SynchronizationContext _unitySynchronizationContext;
        private static MainThreadDispatcherSettings _settings = new MainThreadDispatcherSettings();
        private static readonly MainThreadWorkQueue WorkQueue = new MainThreadWorkQueue(_settings);

        private static int _state = (int)MainThreadDispatcherLifecycleState.Uninitialized;
        private static int _mainThreadManagedId;
        private static int _lastDispatchFrame = -1;
        private static int _playerLoopInstalled;
        private static int _editorHooksRegistered;

        private static long _executedUserItems;
        private static long _executedControlItems;
        private static long _dispatchExceptions;
        private static long _queueLatencyTicksTotal;
        private static long _queueLatencySamples;

        private static MainThreadDispatcherLifecycleState State =>
            (MainThreadDispatcherLifecycleState)Volatile.Read(ref _state);

        /// <summary>
        /// Returns the singleton dispatcher instance.
        /// </summary>
        public static MainThreadDispatcher Instance => EnsureInstanceReady();

        /// <summary>
        /// Returns the active dispatcher lifecycle state.
        /// </summary>
        public static MainThreadDispatcherLifecycleState LifecycleState => State;

        /// <summary>
        /// Gets a clone of current dispatcher settings.
        /// </summary>
        public static MainThreadDispatcherSettings GetSettings()
        {
            lock (LifecycleLock)
            {
                return _settings.Clone();
            }
        }

        /// <summary>
        /// Applies runtime dispatcher settings.
        /// </summary>
        public static void Configure(MainThreadDispatcherSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var clone = settings.Clone();
            clone.Validate();

            lock (LifecycleLock)
            {
                _settings = clone;
                WorkQueue.Reconfigure(clone);
            }
        }

        /// <summary>
        /// Returns queue and dispatch runtime metrics.
        /// </summary>
        public static MainThreadDispatcherMetrics GetMetrics()
        {
            var queue = WorkQueue.SnapshotMetrics();
            var samples = Interlocked.Read(ref _queueLatencySamples);
            var totalTicks = Interlocked.Read(ref _queueLatencyTicksTotal);
            var avgMs = samples <= 0
                ? 0d
                : (totalTicks * 1000d) / (samples * Stopwatch.Frequency);

            return new MainThreadDispatcherMetrics(
                lifecycleState: State,
                mainThreadManagedId: Volatile.Read(ref _mainThreadManagedId),
                hasUnitySynchronizationContext: _unitySynchronizationContext != null,
                executedUserItems: Interlocked.Read(ref _executedUserItems),
                executedControlItems: Interlocked.Read(ref _executedControlItems),
                dispatchExceptions: Interlocked.Read(ref _dispatchExceptions),
                averageQueueLatencyMs: avgMs,
                queue: queue);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            TryUninstallPlayerLoop();

            Volatile.Write(ref _state, (int)MainThreadDispatcherLifecycleState.Uninitialized);
            Volatile.Write(ref _mainThreadManagedId, 0);
            _unitySynchronizationContext = null;
            _instance = null;
            _lastDispatchFrame = -1;
            Interlocked.Exchange(ref _playerLoopInstalled, 0);

            Interlocked.Exchange(ref _executedUserItems, 0);
            Interlocked.Exchange(ref _executedControlItems, 0);
            Interlocked.Exchange(ref _dispatchExceptions, 0);
            Interlocked.Exchange(ref _queueLatencyTicksTotal, 0);
            Interlocked.Exchange(ref _queueLatencySamples, 0);

            WorkQueue.FailAll(new OperationCanceledException(
                "MainThreadDispatcher static state reset during domain reload."));
            WorkQueue.Reset();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BootstrapBeforeSceneLoad()
        {
            if (!Application.isPlaying)
                return;

            CaptureMainThreadIdentity(requireUnitySynchronizationContext: true);
            EnsureInstanceReady();
        }

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void RegisterEditorReloadHooks()
        {
            if (Interlocked.Exchange(ref _editorHooksRegistered, 1) != 0)
                return;

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
            CaptureMainThreadIdentity(requireUnitySynchronizationContext: true);

            DontDestroyOnLoad(gameObject);

            Application.lowMemory -= HandleLowMemory;
            Application.lowMemory += HandleLowMemory;

            TryInstallPlayerLoop();
            Volatile.Write(ref _state, (int)MainThreadDispatcherLifecycleState.Ready);
        }

        private void Update()
        {
            // Fallback dispatch path when PlayerLoop installation is unavailable.
            if (Volatile.Read(ref _playerLoopInstalled) == 0)
            {
                DispatchQueuedWorkOncePerFrame();
            }
        }

        private void OnApplicationQuit()
        {
            if (!ReferenceEquals(_instance, this))
                return;

            BeginShutdown(
                "MainThreadDispatcher shutting down during application quit.",
                MainThreadDispatcherLifecycleState.Disposing);
        }

        private void OnDestroy()
        {
            if (!ReferenceEquals(_instance, this))
                return;

            Application.lowMemory -= HandleLowMemory;

            BeginShutdown(
                "MainThreadDispatcher was destroyed; pending work was canceled.",
                MainThreadDispatcherLifecycleState.Disposing);
        }

        /// <summary>
        /// Enqueues fire-and-forget work on the main thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            ObserveBackgroundTask(ExecuteAsync(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }));
        }

        /// <summary>
        /// Executes user-plane work on the main thread and returns a completion task.
        /// </summary>
        public static Task ExecuteAsync(
            Action action,
            CancellationToken cancellationToken = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            EnsureInitializedIfMainThread();
            RejectIfUnavailable();

            var settings = GetSettings();
            if (settings.AllowInlineExecutionOnMainThread && IsMainThread())
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

            return ExecuteQueuedAsync(action, cancellationToken, MainThreadDispatcherWorkKind.User);
        }

        /// <summary>
        /// Executes control-plane work on the main thread.
        /// Control-plane items are isolated from user queue backpressure.
        /// </summary>
        public static Task ExecuteControlAsync(
            Action action,
            CancellationToken cancellationToken = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            EnsureInitializedIfMainThread();
            RejectIfUnavailable();

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

            return ExecuteQueuedAsync(action, cancellationToken, MainThreadDispatcherWorkKind.Control);
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
            RejectIfUnavailable();

            var settings = GetSettings();
            if (settings.AllowInlineExecutionOnMainThread && IsMainThread())
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

            return ExecuteQueuedAsync(func, cancellationToken, MainThreadDispatcherWorkKind.User);
        }

        /// <summary>
        /// Synchronously executes work on the main thread.
        /// </summary>
        /// <remarks>
        /// This API is intentionally main-thread only to avoid worker-thread starvation.
        /// Use <see cref="ExecuteAsync(Action, CancellationToken)"/> from worker threads.
        /// </remarks>
        public static void Execute(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            EnsureInitializedIfMainThread();
            RejectIfUnavailable();

            if (!IsMainThread())
            {
                throw new InvalidOperationException(
                    "MainThreadDispatcher.Execute can only be called from the Unity main thread. " +
                    "Use ExecuteAsync for worker-thread callers.");
            }

            action();
        }

        /// <summary>
        /// Synchronously executes value-producing work on the main thread.
        /// </summary>
        /// <remarks>
        /// This API is intentionally main-thread only to avoid worker-thread starvation.
        /// Use <see cref="ExecuteAsync{T}(Func{T}, CancellationToken)"/> from worker threads.
        /// </remarks>
        public static T Execute<T>(Func<T> func)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));

            EnsureInitializedIfMainThread();
            RejectIfUnavailable();

            if (!IsMainThread())
            {
                throw new InvalidOperationException(
                    "MainThreadDispatcher.Execute<T> can only be called from the Unity main thread. " +
                    "Use ExecuteAsync<T> for worker-thread callers.");
            }

            return func();
        }

        /// <summary>
        /// Returns true when called from the captured Unity main thread.
        /// </summary>
        public static bool IsMainThread()
        {
            var captured = Volatile.Read(ref _mainThreadManagedId);
            return captured > 0 && Thread.CurrentThread.ManagedThreadId == captured;
        }

        private static async Task ExecuteQueuedAsync(
            Action action,
            CancellationToken cancellationToken,
            MainThreadDispatcherWorkKind workKind)
        {
            var tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var cancellationRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
                : default;

            var workItem = new DispatchWorkItem(
                execute: () =>
                {
                    if (tcs.Task.IsCompleted)
                    {
                        cancellationRegistration.Dispose();
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
                        cancellationRegistration.Dispose();
                    }
                },
                fail: exception =>
                {
                    cancellationRegistration.Dispose();
                    if (exception is OperationCanceledException oce)
                    {
                        tcs.TrySetCanceled(oce.CancellationToken);
                    }
                    else
                    {
                        tcs.TrySetException(exception);
                    }
                });

            try
            {
                await WorkQueue.EnqueueAsync(workItem, workKind, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancellationRegistration.Dispose();
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                cancellationRegistration.Dispose();
                tcs.TrySetException(ex);
            }

            await tcs.Task.ConfigureAwait(false);
        }

        private static async Task<T> ExecuteQueuedAsync<T>(
            Func<T> func,
            CancellationToken cancellationToken,
            MainThreadDispatcherWorkKind workKind)
        {
            var tcs = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var cancellationRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
                : default;

            var workItem = new DispatchWorkItem(
                execute: () =>
                {
                    if (tcs.Task.IsCompleted)
                    {
                        cancellationRegistration.Dispose();
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
                        cancellationRegistration.Dispose();
                    }
                },
                fail: exception =>
                {
                    cancellationRegistration.Dispose();
                    if (exception is OperationCanceledException oce)
                    {
                        tcs.TrySetCanceled(oce.CancellationToken);
                    }
                    else
                    {
                        tcs.TrySetException(exception);
                    }
                });

            try
            {
                await WorkQueue.EnqueueAsync(workItem, workKind, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancellationRegistration.Dispose();
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                cancellationRegistration.Dispose();
                tcs.TrySetException(ex);
            }

            return await tcs.Task.ConfigureAwait(false);
        }

        private static void ObserveBackgroundTask(Task task)
        {
            if (task == null)
                return;

            if (task.IsCompleted)
            {
                if (task.IsFaulted && task.Exception != null)
                    Debug.LogException(task.Exception.GetBaseException());
                return;
            }

            task.ContinueWith(
                continuationAction: t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        Debug.LogException(t.Exception.GetBaseException());
                },
                cancellationToken: CancellationToken.None,
                continuationOptions: TaskContinuationOptions.ExecuteSynchronously,
                scheduler: TaskScheduler.Default);
        }

        private static MainThreadDispatcher EnsureInstanceReady()
        {
            var instance = _instance;
            if (instance != null && State == MainThreadDispatcherLifecycleState.Ready)
                return instance;

            lock (LifecycleLock)
            {
                if (_instance != null && State == MainThreadDispatcherLifecycleState.Ready)
                    return _instance;

                RejectIfUnavailable();

                CaptureMainThreadIdentity(requireUnitySynchronizationContext: true);
                if (!IsMainThread())
                {
                    throw new InvalidOperationException(
                        "MainThreadDispatcher is not initialized yet and cannot be created " +
                        "from a worker thread. Access MainThreadDispatcher.Instance from the " +
                        "Unity main thread during startup before dispatching background work.");
                }

                Volatile.Write(ref _state, (int)MainThreadDispatcherLifecycleState.Initializing);

                var go = new GameObject("[TurboHTTP MainThreadDispatcher]");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<MainThreadDispatcher>();

                if (State == MainThreadDispatcherLifecycleState.Initializing)
                {
                    Volatile.Write(ref _state, (int)MainThreadDispatcherLifecycleState.Ready);
                }

                return _instance;
            }
        }

        private static void EnsureInitializedIfMainThread()
        {
            if (State != MainThreadDispatcherLifecycleState.Uninitialized)
                return;

            CaptureMainThreadIdentity(requireUnitySynchronizationContext: false);

            if (IsMainThread())
            {
                EnsureInstanceReady();
                return;
            }

            throw new InvalidOperationException(
                "MainThreadDispatcher is not initialized yet and cannot be used from worker threads. " +
                "Access MainThreadDispatcher.Instance on the Unity main thread during startup first.");
        }

        private static void CaptureMainThreadIdentity(bool requireUnitySynchronizationContext)
        {
            if (Volatile.Read(ref _mainThreadManagedId) > 0 && _unitySynchronizationContext != null)
                return;

            if (Thread.CurrentThread.IsThreadPoolThread)
            {
                if (requireUnitySynchronizationContext)
                {
                    throw new InvalidOperationException(
                        "MainThreadDispatcher main thread has not been initialized. " +
                        "Initialize it from Unity's main thread first by touching " +
                        "MainThreadDispatcher.Instance during startup.");
                }

                return;
            }

            var currentContext = SynchronizationContext.Current;
            var hasUnityContext = IsUnitySynchronizationContext(currentContext);

            if (requireUnitySynchronizationContext && !hasUnityContext)
            {
                throw new InvalidOperationException(
                    "MainThreadDispatcher failed initialization because UnitySynchronizationContext " +
                    "was not available on startup. Ensure dispatcher bootstrap runs on Unity's " +
                    "main thread before scheduling work.");
            }

            if (!hasUnityContext)
                return;

            _unitySynchronizationContext = currentContext;

            Interlocked.CompareExchange(
                ref _mainThreadManagedId,
                Thread.CurrentThread.ManagedThreadId,
                0);
        }

        private static bool IsUnitySynchronizationContext(SynchronizationContext context)
        {
            if (context == null)
                return false;

            var typeName = context.GetType().FullName;
            return !string.IsNullOrEmpty(typeName) &&
                   typeName.IndexOf("UnitySynchronizationContext", StringComparison.Ordinal) >= 0;
        }

        private static void BeginShutdown(string reason, MainThreadDispatcherLifecycleState terminalState)
        {
            lock (LifecycleLock)
            {
                var current = State;
                if (current == MainThreadDispatcherLifecycleState.Disposing ||
                    current == MainThreadDispatcherLifecycleState.Reloading)
                {
                    return;
                }

                Volatile.Write(ref _state, (int)terminalState);
                WorkQueue.RejectNewWork(CreateUnavailableMessage(terminalState));
                _instance = null;
            }

            WorkQueue.FailAll(new OperationCanceledException(reason));
        }

        private static void RejectIfUnavailable()
        {
            var current = State;
            if (current == MainThreadDispatcherLifecycleState.Disposing ||
                current == MainThreadDispatcherLifecycleState.Reloading)
            {
                throw new InvalidOperationException(CreateUnavailableMessage(current));
            }
        }

        private static string CreateUnavailableMessage(MainThreadDispatcherLifecycleState state)
        {
            switch (state)
            {
                case MainThreadDispatcherLifecycleState.Disposing:
                    return "MainThreadDispatcher is disposing. New work is rejected.";
                case MainThreadDispatcherLifecycleState.Reloading:
                    return "MainThreadDispatcher is reloading. New work is rejected.";
                case MainThreadDispatcherLifecycleState.Initializing:
                    return "MainThreadDispatcher is still initializing. Retry after initialization completes.";
                default:
                    return "MainThreadDispatcher is unavailable. Ensure it is initialized on the Unity main thread.";
            }
        }

        private static void TryInstallPlayerLoop()
        {
            if (Interlocked.CompareExchange(ref _playerLoopInstalled, 1, 0) != 0)
                return;

            try
            {
                var loop = PlayerLoop.GetCurrentPlayerLoop();
                if (ContainsPlayerLoopType(loop, typeof(PlayerLoopDispatchMarker)))
                    return;

                var subsystems = loop.subSystemList ?? Array.Empty<PlayerLoopSystem>();
                var updated = new PlayerLoopSystem[subsystems.Length + 1];
                Array.Copy(subsystems, updated, subsystems.Length);

                updated[subsystems.Length] = new PlayerLoopSystem
                {
                    type = typeof(PlayerLoopDispatchMarker),
                    updateDelegate = PlayerLoopDispatch
                };

                loop.subSystemList = updated;
                PlayerLoop.SetPlayerLoop(loop);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _playerLoopInstalled, 0);
                Debug.LogWarning(
                    "[TurboHTTP] MainThreadDispatcher failed to install PlayerLoop hook; " +
                    "falling back to MonoBehaviour.Update. Error: " + ex.Message);
            }
        }

        private static void TryUninstallPlayerLoop()
        {
            try
            {
                var loop = PlayerLoop.GetCurrentPlayerLoop();
                if (!ContainsPlayerLoopType(loop, typeof(PlayerLoopDispatchMarker)))
                    return;

                var subsystems = loop.subSystemList;
                if (subsystems == null || subsystems.Length == 0)
                    return;

                var filtered = new System.Collections.Generic.List<PlayerLoopSystem>(subsystems.Length);
                for (var i = 0; i < subsystems.Length; i++)
                {
                    if (subsystems[i].type != typeof(PlayerLoopDispatchMarker))
                        filtered.Add(subsystems[i]);
                }

                loop.subSystemList = filtered.ToArray();
                PlayerLoop.SetPlayerLoop(loop);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[TurboHTTP] Failed to uninstall PlayerLoop hook: " + ex.Message);
            }
        }

        private static bool ContainsPlayerLoopType(PlayerLoopSystem root, Type type)
        {
            if (root.type == type)
                return true;

            var subsystems = root.subSystemList;
            if (subsystems == null || subsystems.Length == 0)
                return false;

            for (var i = 0; i < subsystems.Length; i++)
            {
                if (ContainsPlayerLoopType(subsystems[i], type))
                    return true;
            }

            return false;
        }

        private static void PlayerLoopDispatch()
        {
            DispatchQueuedWorkOncePerFrame();
        }

        private static void DispatchQueuedWorkOncePerFrame()
        {
            if (State != MainThreadDispatcherLifecycleState.Ready)
                return;

            if (_instance == null || !IsMainThread())
                return;

            var frame = Time.frameCount;
            if (frame == Volatile.Read(ref _lastDispatchFrame))
                return;

            Volatile.Write(ref _lastDispatchFrame, frame);
            _instance.DrainQueueWithinFrameBudget();
        }

        private void DrainQueueWithinFrameBudget()
        {
            if (!ReferenceEquals(_instance, this))
                return;

            var settings = GetSettings();
            var maxItems = settings.MaxItemsPerFrame;
            var budgetTicks = (long)Math.Max(1d, settings.MaxWorkTimeMs * Stopwatch.Frequency / 1000d);

            var processed = 0;
            var startTicks = Stopwatch.GetTimestamp();

            while (processed < maxItems)
            {
                if (Stopwatch.GetTimestamp() - startTicks >= budgetTicks)
                    break;

                if (!WorkQueue.TryDequeue(out var dequeued))
                    break;

                var now = Stopwatch.GetTimestamp();
                var latencyTicks = now - dequeued.EnqueueTimestamp;
                if (latencyTicks > 0)
                {
                    Interlocked.Add(ref _queueLatencyTicksTotal, latencyTicks);
                    Interlocked.Increment(ref _queueLatencySamples);
                }

                try
                {
                    dequeued.WorkItem.Execute();
                    if (dequeued.Kind == MainThreadDispatcherWorkKind.Control)
                        Interlocked.Increment(ref _executedControlItems);
                    else
                        Interlocked.Increment(ref _executedUserItems);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _dispatchExceptions);
                    Debug.LogException(ex);
                }

                processed++;
            }
        }

        private static void HandleLowMemory()
        {
            if (State != MainThreadDispatcherLifecycleState.Ready)
                return;

            MainThreadDispatcherSettings settings;
            lock (LifecycleLock)
            {
                settings = _settings.Clone();
            }

            var dropped = WorkQueue.DropUserWork(
                new OperationCanceledException(
                    "MainThreadDispatcher dropped queued user work due to low-memory pressure."),
                settings.LowMemoryDropCount);

            if (dropped > 0)
            {
                Debug.LogWarning(
                    "[TurboHTTP] MainThreadDispatcher low-memory shedding dropped " +
                    dropped +
                    " queued user work items.");
            }
        }

#if UNITY_EDITOR
        private static void HandleBeforeAssemblyReload()
        {
            BeginShutdown(
                "MainThreadDispatcher canceled queued work because the Unity editor domain is reloading.",
                MainThreadDispatcherLifecycleState.Reloading);
        }

        private static void HandlePlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                BeginShutdown(
                    "MainThreadDispatcher canceled queued work because Play Mode is exiting.",
                    MainThreadDispatcherLifecycleState.Reloading);
            }
        }
#endif
    }
}
