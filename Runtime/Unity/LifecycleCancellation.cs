using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Source of lifecycle cancellation for coroutine-bound operations.
    /// </summary>
    public enum LifecycleCancellationReason
    {
        None = 0,
        ExplicitToken = 1,
        OwnerDestroyed = 2,
        OwnerInactive = 3
    }

    /// <summary>
    /// Represents a bound lifecycle cancellation token.
    /// </summary>
    public sealed class LifecycleCancellationBinding : IDisposable
    {
        private readonly Action<LifecycleCancellationBinding> _onDispose;
        private readonly CancellationTokenSource _cts;
        private readonly CancellationTokenRegistration _externalRegistration;
        private int _reason;
        private int _disposed;

        internal LifecycleCancellationBinding(
            UnityEngine.Object owner,
            bool cancelOnOwnerInactive,
            CancellationToken externalToken,
            Action<LifecycleCancellationBinding> onDispose)
        {
            Owner = owner;
            CancelOnOwnerInactive = cancelOnOwnerInactive;
            _onDispose = onDispose;
            _cts = new CancellationTokenSource();

            if (externalToken.CanBeCanceled)
            {
                _externalRegistration = externalToken.Register(() =>
                {
                    Cancel(LifecycleCancellationReason.ExplicitToken);
                });
            }

            if (externalToken.IsCancellationRequested)
            {
                Cancel(LifecycleCancellationReason.ExplicitToken);
            }
        }

        internal UnityEngine.Object Owner { get; }
        internal bool CancelOnOwnerInactive { get; }

        public CancellationToken Token => _cts.Token;

        public LifecycleCancellationReason Reason =>
            (LifecycleCancellationReason)Volatile.Read(ref _reason);

        public bool IsCancellationRequested => _cts.IsCancellationRequested;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal void Cancel(LifecycleCancellationReason reason)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (reason == LifecycleCancellationReason.None)
                reason = LifecycleCancellationReason.ExplicitToken;

            if (Interlocked.CompareExchange(ref _reason, (int)reason, 0) != 0)
                return;

            try
            {
                if (Volatile.Read(ref _disposed) == 0)
                    _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Dispose race — cancellation lost because binding is already disposed.
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _onDispose?.Invoke(this);
            _externalRegistration.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Creates and monitors lifecycle cancellation bindings for coroutine wrappers.
    /// </summary>
    public static class LifecycleCancellation
    {
        private static readonly object Gate = new object();
        private static readonly List<LifecycleCancellationBinding> ActiveBindings =
            new List<LifecycleCancellationBinding>();
        private static readonly List<PendingCancellation> PendingCancellations =
            new List<PendingCancellation>();

        private static LifecycleCancellationDriver _driver;
        private static int _driverEnsureQueued;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            lock (Gate)
            {
                ActiveBindings.Clear();
                PendingCancellations.Clear();
            }

            _driver = null;
            Interlocked.Exchange(ref _driverEnsureQueued, 0);
        }

        public static LifecycleCancellationBinding Bind(
            UnityEngine.Object owner,
            CancellationToken externalToken = default,
            bool cancelOnOwnerInactive = false)
        {
            var binding = new LifecycleCancellationBinding(
                owner,
                cancelOnOwnerInactive,
                externalToken,
                RemoveBinding);

            if (owner == null)
            {
                return binding;
            }

            RequestDriverEnsure();

            lock (Gate)
            {
                ActiveBindings.Add(binding);
            }

            return binding;
        }

        internal static void Poll()
        {
            lock (Gate)
            {
                PendingCancellations.Clear();

                for (var i = ActiveBindings.Count - 1; i >= 0; i--)
                {
                    var binding = ActiveBindings[i];
                    if (binding == null || binding.IsDisposed)
                    {
                        ActiveBindings.RemoveAt(i);
                        continue;
                    }

                    if (binding.IsCancellationRequested)
                    {
                        ActiveBindings.RemoveAt(i);
                        continue;
                    }

                    var owner = binding.Owner;
                    if (owner == null)
                    {
                        ActiveBindings.RemoveAt(i);
                        PendingCancellations.Add(new PendingCancellation(
                            binding,
                            LifecycleCancellationReason.OwnerDestroyed));
                        continue;
                    }

                    if (binding.CancelOnOwnerInactive && IsOwnerInactive(owner))
                    {
                        ActiveBindings.RemoveAt(i);
                        PendingCancellations.Add(new PendingCancellation(
                            binding,
                            LifecycleCancellationReason.OwnerInactive));
                    }
                }
            }

            for (var i = 0; i < PendingCancellations.Count; i++)
            {
                var cancellation = PendingCancellations[i];
                cancellation.Binding.Cancel(cancellation.Reason);
            }
        }

        private static void RequestDriverEnsure()
        {
            if (_driver != null)
                return;

            if (MainThreadDispatcher.IsMainThread())
            {
                EnsureDriver();
                return;
            }

            if (Interlocked.CompareExchange(ref _driverEnsureQueued, 1, 0) != 0)
                return;

            try
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    Interlocked.Exchange(ref _driverEnsureQueued, 0);
                    EnsureDriver();
                });
            }
            catch
            {
                Interlocked.Exchange(ref _driverEnsureQueued, 0);
            }
        }

        private static void EnsureDriver()
        {
            if (_driver != null)
                return;

            var go = new GameObject("[TurboHTTP LifecycleCancellation]");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _driver = go.AddComponent<LifecycleCancellationDriver>();
        }

        private static bool IsOwnerInactive(UnityEngine.Object owner)
        {
            if (owner == null)
                return true;

            if (owner is GameObject go)
                return !go.activeInHierarchy;

            if (owner is Behaviour behaviour)
                return behaviour.gameObject == null || !behaviour.gameObject.activeInHierarchy;

            if (owner is Component component)
                return component.gameObject == null || !component.gameObject.activeInHierarchy;

            return false;
        }

        private static void RemoveBinding(LifecycleCancellationBinding binding)
        {
            if (binding == null)
                return;

            lock (Gate)
            {
                for (var i = ActiveBindings.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(ActiveBindings[i], binding))
                    {
                        ActiveBindings.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        private sealed class LifecycleCancellationDriver : MonoBehaviour
        {
            private void Update()
            {
                LifecycleCancellation.Poll();
            }

            private void OnDestroy()
            {
                if (ReferenceEquals(_driver, this))
                {
                    _driver = null;
                }
            }
        }

        private readonly struct PendingCancellation
        {
            public PendingCancellation(
                LifecycleCancellationBinding binding,
                LifecycleCancellationReason reason)
            {
                Binding = binding;
                Reason = reason;
            }

            public LifecycleCancellationBinding Binding { get; }
            public LifecycleCancellationReason Reason { get; }
        }
    }
}
