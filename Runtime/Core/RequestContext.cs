using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Timeline event representing a stage in request execution.
    /// Used for observability and debugging.
    /// </summary>
    public class TimelineEvent
    {
        public string Name { get; }
        public TimeSpan Timestamp { get; }
        public Dictionary<string, object> Data { get; }

        public TimelineEvent(string name, TimeSpan timestamp, Dictionary<string, object> data = null)
        {
            Name = name;
            Timestamp = timestamp;
            Data = data ?? new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Execution context for a single HTTP request.
    /// Tracks timeline events, metadata, and state across middleware.
    /// Thread-safe for concurrent access from async continuations.
    /// </summary>
    public class RequestContext
    {
        private readonly Stopwatch _stopwatch;
        private readonly List<TimelineEvent> _timeline;
        private readonly Dictionary<string, object> _state;
        private readonly object _lock = new object();

        public UHttpRequest Request { get; private set; }

        public IReadOnlyList<TimelineEvent> Timeline
        {
            get
            {
                lock (_lock)
                {
                    return _timeline.ToArray();
                }
            }
        }

        public IReadOnlyDictionary<string, object> State
        {
            get
            {
                lock (_lock)
                {
                    return new Dictionary<string, object>(_state);
                }
            }
        }

        /// <summary>
        /// Time elapsed since request started.
        /// </summary>
        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public RequestContext(UHttpRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            _stopwatch = Stopwatch.StartNew();
            _timeline = new List<TimelineEvent>();
            _state = new Dictionary<string, object>();
        }

        /// <summary>
        /// Record a timeline event.
        /// </summary>
        public void RecordEvent(string eventName, Dictionary<string, object> data = null)
        {
            var evt = new TimelineEvent(eventName, _stopwatch.Elapsed, data);
            lock (_lock)
            {
                _timeline.Add(evt);
            }
        }

        /// <summary>
        /// Update the request (used by middleware that transforms requests).
        /// </summary>
        public void UpdateRequest(UHttpRequest newRequest)
        {
            Request = newRequest ?? throw new ArgumentNullException(nameof(newRequest));
        }

        /// <summary>
        /// Store data in the context state.
        /// This allows middleware to communicate with each other.
        /// </summary>
        public void SetState(string key, object value)
        {
            lock (_lock)
            {
                _state[key] = value;
            }
        }

        /// <summary>
        /// Retrieve data from the context state.
        /// </summary>
        public T GetState<T>(string key, T defaultValue = default)
        {
            lock (_lock)
            {
                if (_state.TryGetValue(key, out var value) && value is T typedValue)
                {
                    return typedValue;
                }
                return defaultValue;
            }
        }

        /// <summary>
        /// Stop the stopwatch and return total elapsed time.
        /// </summary>
        public TimeSpan Stop()
        {
            _stopwatch.Stop();
            return _stopwatch.Elapsed;
        }
    }
}
