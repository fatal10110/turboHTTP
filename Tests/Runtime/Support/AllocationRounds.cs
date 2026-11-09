using System;
using System.Collections.Generic;

namespace TurboHTTP.Tests
{
    /// <summary>The outcome of comparing an allocation reading with a budget.</summary>
    internal enum AllocationVerdict
    {
        /// <summary>The section allocated within the budget.</summary>
        Within,

        /// <summary>The section allocated over the budget, even without the frame's foreign allocations.</summary>
        Over,

        /// <summary>
        /// Over the budget only with the frame's foreign allocations counted: the counter cannot tell whether the
        /// section or other work in the frame allocated the excess.
        /// </summary>
        Unresolved,
    }

    /// <summary>
    /// The readings of one allocation gate (#438). The section runs once per round, each round alone in its own
    /// frame. Other work only adds to a reading, so the smallest round is the tightest upper bound of the
    /// section's own cost, and an allocation the section makes on every unit shows in every round. Where the
    /// counter sees the whole frame, idle frames (no section) give the smallest foreign cost of a frame.
    /// </summary>
    internal sealed class AllocationRounds
    {
        private readonly List<long> _roundBytesPerUnit = new List<long>();
        private readonly List<long> _idleFrameBytes = new List<long>();
        private long _bestTotalBytes = -1;
        private int _bestUnits;

        /// <summary>Bytes per unit of the smallest round, or -1 before the first round.</summary>
        internal long BytesPerUnit { get; private set; } = -1;

        /// <summary>The unit count of the smallest round.</summary>
        internal int Units => _bestUnits;

        /// <summary>The total bytes of the smallest round, or -1 before the first round.</summary>
        internal long TotalBytes => _bestTotalBytes;

        /// <summary>The smallest idle frame, or 0 when none was recorded (a counter that sees only the section).</summary>
        internal long ForeignFloorBytes { get; private set; }

        /// <summary>Records one round: <paramref name="totalBytes"/> allocated over <paramref name="units"/> units.</summary>
        internal void AddRound(long totalBytes, int units)
        {
            if (units <= 0)
                throw new ArgumentOutOfRangeException(nameof(units), "A round must run at least one unit.");

            var total = Math.Max(0L, totalBytes);
            var perUnit = total / units;
            _roundBytesPerUnit.Add(perUnit);
            if (BytesPerUnit < 0 || perUnit < BytesPerUnit)
            {
                BytesPerUnit = perUnit;
                _bestTotalBytes = total;
                _bestUnits = units;
            }
        }

        /// <summary>Records what the counter saw in a frame that ran no section.</summary>
        internal void AddIdleFrame(long bytes)
        {
            var value = Math.Max(0L, bytes);
            ForeignFloorBytes = _idleFrameBytes.Count == 0 ? value : Math.Min(ForeignFloorBytes, value);
            _idleFrameBytes.Add(value);
        }

        /// <summary>
        /// <see cref="AllocationVerdict.Within"/> when the smallest round fits <paramref name="budgetBytesPerUnit"/>;
        /// <see cref="AllocationVerdict.Unresolved"/> when it fits only after taking off the smallest idle frame;
        /// else <see cref="AllocationVerdict.Over"/>.
        /// </summary>
        internal AllocationVerdict Judge(long budgetBytesPerUnit)
        {
            if (BytesPerUnit < 0)
                throw new InvalidOperationException("No round was recorded.");

            if (BytesPerUnit <= budgetBytesPerUnit)
                return AllocationVerdict.Within;

            var withoutForeign = Math.Max(0L, _bestTotalBytes - ForeignFloorBytes) / _bestUnits;
            return withoutForeign <= budgetBytesPerUnit ? AllocationVerdict.Unresolved : AllocationVerdict.Over;
        }

        /// <summary>Every round's bytes per unit and every idle frame, for the test log.</summary>
        internal string Describe()
        {
            return "rounds=[" + string.Join(",", _roundBytesPerUnit) + "] idle_frames=[" +
                string.Join(",", _idleFrameBytes) + "]";
        }
    }
}
