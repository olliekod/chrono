using System;
using System.Collections.Generic;

namespace ChronoRecorder
{
    public readonly record struct SegmentSpan(string Path, double DurationSeconds);

    /// <summary>
    /// SegmentSpans to concatenate (oldest first) and how far into the joined footage the clip starts.
    /// </summary>
    public sealed record ClipPlan(
        IReadOnlyList<SegmentSpan> Segments,
        double SeekSeconds,
        double AvailableSeconds,
        bool IsShort);

    public static class ClipPlanner
    {
        /// <summary>
        /// Pick the newest segments covering <paramref name="clipLengthSeconds"/>. Segments are whole files,
        /// so the footage usually overshoots; <see cref="ClipPlan.SeekSeconds"/> says how much to skip
        /// from the start so the newest footage is the part that's kept.
        /// </summary>
        public static ClipPlan? Plan(IReadOnlyList<SegmentSpan> segmentsOldestFirst, double clipLengthSeconds)
        {
            if (clipLengthSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(clipLengthSeconds), "Clip length must be positive.");

            if (segmentsOldestFirst.Count == 0)
                return null;

            var picked = new List<SegmentSpan>();
            double collected = 0;

            for (int i = segmentsOldestFirst.Count - 1; i >= 0; i--)
            {
                picked.Insert(0, segmentsOldestFirst[i]);
                collected += segmentsOldestFirst[i].DurationSeconds;

                if (collected >= clipLengthSeconds)
                    break;
            }

            bool isShort = collected < clipLengthSeconds;
            double seek = isShort ? 0 : collected - clipLengthSeconds;

            return new ClipPlan(picked, seek, collected, isShort);
        }
    }
}
