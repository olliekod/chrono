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
    /// the same T0. Each chunk is timestamped as the device delivers it, and that timestamp is used to *place* sound:
    ///  - the first sound after silence goes exactly where it was captured (silence inserted up to it, or its head
    ///    trimmed if the stream is already past it),
    ///  - while sound keeps flowing, chunks are written back to back, exactly as the device supplied them. The
    ///    device's samples are contiguous; only their delivery time wobbles (tens of ms, more when a game loads the
    ///    CPU). Realigning to every wobble punched a hole into the sound every ~200 ms and dropped the overlaps: the
    ///    "bobbing in and out" heard in real recordings. The stream is only realigned if it has drifted far,
    ///  - and silence is padded while the device is quiet (loopback capture delivers nothing during silence), but
    ///    only after it has been quiet for a while, so a late chunk is never mistaken for silence.
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

        /// <summary>While sound is flowing, a chunk whose delivery time says it belongs this far from where the
        /// previous one ended is still written as it is (the wobble of delivery times). Farther means something
        /// really happened (a stall, a device switch), and the stream is realigned. The first sound after silence
        /// is always placed exactly.</summary>
        private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(250);

        /// <summary>Idle: silence is only written once the stream is this far behind the clock, and only up to that
        /// margin. Longer than the delivery wobble, so a late chunk still finds its place open.</summary>
        private static readonly TimeSpan IdleMargin = TimeSpan.FromMilliseconds(300);

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

        /// <summary>Seconds-since-T0 span to silence in the recording, or null. Guarded by <see cref="gate"/>.</summary>
        private (double Start, double End)? muteWindow;

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

        /// <summary>
        /// Silences the recording (not the live device: this only touches what gets written to FFmpeg) from now
        /// until <paramref name="duration"/> later. For a sound Chrono itself plays through the same output this
        /// feeder is capturing, so it is heard but never recorded - see <see cref="ClipCue"/>. The window is in this
        /// feeder's own clock, so it stays correct however delivery is currently running behind or ahead.
        /// </summary>
        public void MuteFromNow(TimeSpan duration)
        {
            var now = clock();
            lock (gate) muteWindow = (now.TotalSeconds, (now + duration).TotalSeconds);
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
                if (muteWindow is { } window) Silence(copy, arrivedAt.TotalSeconds, window);

                pending.Enqueue(new Chunk(copy, arrivedAt));
                pendingBytes += count;

                long maxBytes = (long)(MaxBuffered.TotalSeconds * sampleRate) * bytesPerFrame;
                while (pendingBytes > maxBytes && pending.Count > 1)
                    pendingBytes -= pending.Dequeue().Data.Length;
            }
        }

        public void Push(byte[] buffer) => Push(buffer, buffer.Length);

        /// <summary>
        /// Zeroes whatever part of this chunk falls inside the mute window (silence is all-zero bytes in both PCM16
        /// and float32, so this needs no format-specific handling), and forgets the window once the stream has moved
        /// entirely past it. Called with <see cref="gate"/> already held.
        /// </summary>
        private void Silence(byte[] data, double chunkEndSeconds, (double Start, double End) window)
        {
            int frames = data.Length / bytesPerFrame;
            double chunkStartSeconds = chunkEndSeconds - (double)frames / sampleRate;

            double overlapStart = Math.Max(chunkStartSeconds, window.Start);
            double overlapEnd = Math.Min(chunkEndSeconds, window.End);
            if (overlapEnd > overlapStart)
            {
                int startFrame = Math.Max(0, (int)Math.Round((overlapStart - chunkStartSeconds) * sampleRate));
                int endFrame = Math.Min(frames, (int)Math.Round((overlapEnd - chunkStartSeconds) * sampleRate));
                if (endFrame > startFrame) Array.Clear(data, startFrame * bytesPerFrame, (endFrame - startFrame) * bytesPerFrame);
            }

            if (chunkStartSeconds >= window.End) muteWindow = null;
        }

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
                long idleThreshold = idleMargin / 3;   // don't write silence in tiny pieces

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
                    if (target - position > idleThreshold) WriteSilence(target - position);

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
