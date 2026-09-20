using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ChronoRecorder
{
    /// <summary>
    /// Feeds captured sound into FFmpeg's audio pipe so that it lines up with the picture.
    ///
    /// The picture is timestamped with the wall clock (seconds since a shared start time T0). This class makes the
    /// audio stream match: <b>the position of a sample in the stream is the time it was captured</b>, measured from
    /// the same T0. It gets there by timestamping each chunk as the device delivers it, and when writing:
    ///  - inserting silence up to the chunk's capture time if the stream is behind it,
    ///  - trimming the chunk's head if the stream is already past it,
    ///  - and padding silence while the device is quiet (loopback capture delivers nothing during silence).
    ///
    /// Because sound is placed by capture time, not write time, nothing that delays the writing can shift it. That
    /// includes FFmpeg taking seconds to start reading and then reading in lumps while it also handles video: in
    /// testing, placing sound by write time made it 0.4-0.5 s late.
    ///
    /// Time and the pipe are injected so all of this is testable without FFmpeg.
    /// </summary>
    public sealed class AudioFeeder
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

        /// <summary>While sound is flowing, a chunk this close to where the previous one ended is written as it is;
        /// farther, it is realigned. Device chunks arrive with a few ms of jitter, which must not become clicks.
        /// (The first sound after silence is always placed exactly.)</summary>
        private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(40);

        /// <summary>While idle, silence is only written up to this far behind the clock, so a chunk delivered a
        /// little late still finds its place open instead of overlapping padding.</summary>
        private static readonly TimeSpan IdleMargin = TimeSpan.FromMilliseconds(100);

        /// <summary>Sound queued behind a stalled pipe is stale; keep only the newest this much.</summary>
        private static readonly TimeSpan MaxBuffered = TimeSpan.FromSeconds(2);

        private sealed record Chunk(byte[] Data, TimeSpan End);

        private readonly Stream output;
        private readonly int sampleRate;
        private readonly int bytesPerFrame;
        private readonly Func<TimeSpan> clock;
        private readonly Action<TimeSpan> sleep;
        private readonly long delayFrames;

        private readonly object gate = new object();
        private readonly Queue<Chunk> pending = new Queue<Chunk>();
        private long pendingBytes;
        private long framesWritten;

        /// <summary>True once the first write has gone through, meaning FFmpeg is reading the pipe.</summary>
        public bool Consuming { get; private set; }

        /// <summary>Frames written so far (sound and silence).</summary>
        public long FramesWritten => Volatile.Read(ref framesWritten);

        /// <summary>How much captured sound is waiting to be written.</summary>
        public double PendingMilliseconds
        {
            get { lock (gate) return pendingBytes / (double)bytesPerFrame / sampleRate * 1000; }
        }

        /// <param name="clock">Time since T0, the start time shared with the video.</param>
        /// <param name="sleep">Waits for the given time.</param>
        /// <param name="delay">Pushes every sound later by this much, to trim a constant offset.</param>
        public AudioFeeder(Stream output, int sampleRate, int bytesPerFrame, Func<TimeSpan> clock, Action<TimeSpan> sleep, TimeSpan delay = default)
        {
            this.output = output;
            this.sampleRate = sampleRate;
            this.bytesPerFrame = bytesPerFrame;
            this.clock = clock;
            this.sleep = sleep;
            delayFrames = (long)(delay.TotalSeconds * sampleRate);
        }

        /// <summary>Sound the device just delivered. Safe to call from the capture thread.</summary>
        public void Push(byte[] buffer, int count)
        {
            count -= count % bytesPerFrame;
            if (count <= 0) return;

            var copy = new byte[count];
            Buffer.BlockCopy(buffer, 0, copy, 0, count);
            var arrivedAt = clock();

            lock (gate)
            {
                pending.Enqueue(new Chunk(copy, arrivedAt));
                pendingBytes += count;

                long maxBytes = (long)(MaxBuffered.TotalSeconds * sampleRate) * bytesPerFrame;
                while (pendingBytes > maxBytes && pending.Count > 1)
                    pendingBytes -= pending.Dequeue().Data.Length;
            }
        }

        public void Push(byte[] buffer) => Push(buffer, buffer.Length);

        /// <summary>Runs until cancelled or the pipe closes. Blocks; call it on its own thread.</summary>
        public void Run(CancellationToken ct)
        {
            try
            {
                var silence = new byte[sampleRate * bytesPerFrame];   // one second of zeros
                long position = 0;                                    // frames in the stream so far
                bool followsSound = false;                            // the last thing written was captured sound, not padding
                long tolerance = (long)(Tolerance.TotalSeconds * sampleRate);
                long idleMargin = (long)(IdleMargin.TotalSeconds * sampleRate);

                void Write(byte[] data, int offset, int count, bool isSound)
                {
                    followsSound = isSound;
                    output.Write(data, offset, count);
                    Consuming = true;
                    position += count / bytesPerFrame;
                    Volatile.Write(ref framesWritten, position);
                }

                void WriteSilence(long frames)
                {
                    while (frames > 0)
                    {
                        int n = (int)Math.Min(frames, sampleRate);
                        Write(silence, 0, n * bytesPerFrame, isSound: false);
                        frames -= n;
                    }
                }

                while (!ct.IsCancellationRequested)
                {
                    foreach (var chunk in TakePending())
                    {
                        int frames = chunk.Data.Length / bytesPerFrame;
                        long startFrame = (long)Math.Round(chunk.End.TotalSeconds * sampleRate) - frames + delayFrames;
                        long gap = startFrame - position;
                        long allowed = followsSound ? tolerance : 0;

                        int skip = 0;
                        if (gap > allowed) WriteSilence(gap);                        // stream is behind: pad up to it
                        else if (gap < -allowed) skip = (int)Math.Min(frames, -gap);  // stream is past it: drop the overlap

                        if (skip < frames)
                            Write(chunk.Data, skip * bytesPerFrame, (frames - skip) * bytesPerFrame, isSound: true);
                    }

                    // Nothing (more) from the device: keep the stream moving with the clock.
                    long target = (long)(clock().TotalSeconds * sampleRate) + delayFrames - idleMargin;
                    if (target - position > tolerance) WriteSilence(target - position);

                    sleep(PollInterval);
                }
            }
            catch (IOException)
            {
                // FFmpeg closed the pipe (recording stopped). Nothing left to do.
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                lock (gate)
                {
                    pending.Clear();
                    pendingBytes = 0;
                }
            }
        }

        private List<Chunk> TakePending()
        {
            lock (gate)
            {
                if (pending.Count == 0) return new List<Chunk>();
                var taken = new List<Chunk>(pending);
                pending.Clear();
                pendingBytes = 0;
                return taken;
            }
        }
    }
}
