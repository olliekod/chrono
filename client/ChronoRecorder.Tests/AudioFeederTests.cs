using System.Linq;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class AudioFeederTests
    {
        private const int Rate = 48000;
        private const int BytesPerFrame = 8;   // stereo float

        /// <summary>Virtual time: sleeping advances it, and it can stop the feeder at a chosen moment.</summary>
        private sealed class FakeClock
        {
            public TimeSpan Now;
            public TimeSpan StopAt = TimeSpan.FromSeconds(30);
            public CancellationTokenSource Cts = new();
            public Action<TimeSpan>? OnSleep;   // called with the new time after every sleep

            public void Sleep(TimeSpan d)
            {
                Now += d;
                OnSleep?.Invoke(Now);
                if (Now >= StopAt) Cts.Cancel();
            }
        }

        /// <summary>A pipe that records what was written and can stall like a pipe nobody is reading.</summary>
        private sealed class FakePipe : Stream
        {
            private readonly FakeClock clock;
            private readonly List<byte> stream = new();
            public TimeSpan? StallOnceAtOrAfter;   // the first write at or after this time blocks for StallFor
            public TimeSpan StallFor;
            public bool Broken;
            public int WriteCount;

            public FakePipe(FakeClock clock) => this.clock = clock;

            public byte[] Bytes => stream.ToArray();
            public long Frames => stream.Count / BytesPerFrame;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Broken) throw new IOException("pipe closed");
                if (StallOnceAtOrAfter is { } at && clock.Now >= at)
                {
                    StallOnceAtOrAfter = null;
                    clock.Now += StallFor;
                }
                WriteCount++;
                stream.AddRange(buffer.AsSpan(offset, count).ToArray());
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private static byte[] Chunk(byte marker, int frames) => Enumerable.Repeat(marker, frames * BytesPerFrame).ToArray();

        private static (AudioFeeder Feeder, FakePipe Pipe, FakeClock Clock) Make(TimeSpan? delay = null)
        {
            var clock = new FakeClock();
            var pipe = new FakePipe(clock);
            var feeder = new AudioFeeder(pipe, Rate, BytesPerFrame, () => clock.Now, clock.Sleep, delay ?? TimeSpan.Zero);
            return (feeder, pipe, clock);
        }

        /// <summary>Frame index at which the marker first appears in the stream, or -1.</summary>
        private static long FirstFrameOf(FakePipe pipe, byte marker)
        {
            int i = Array.IndexOf(pipe.Bytes, marker);
            return i < 0 ? -1 : i / BytesPerFrame;
        }

        private static int CountOf(FakePipe pipe, byte marker) => pipe.Bytes.Count(b => b == marker) / BytesPerFrame;

        /// <summary>Hands the feeder a chunk of sound at a given moment, as if the device had just delivered it.</summary>
        private static void PushAt(FakeClock clock, AudioFeeder feeder, double seconds, byte marker, int frames)
        {
            var done = false;
            var previous = clock.OnSleep;
            clock.OnSleep = now =>
            {
                previous?.Invoke(now);
                if (!done && now.TotalSeconds >= seconds) { done = true; feeder.Push(Chunk(marker, frames)); }
            };
        }

        // ------------------------------------------------------------------ silence

        [Fact]
        public void WithNoSound_TheStreamKeepsUpWithTheClock()
        {
            var (feeder, pipe, clock) = Make();
            clock.StopAt = TimeSpan.FromSeconds(2);

            feeder.Run(clock.Cts.Token);

            // Two seconds of time, two seconds of audio (a little behind on purpose: see the idle margin).
            Assert.InRange(pipe.Frames, (long)(1.55 * Rate), 2 * Rate);
            Assert.All(pipe.Bytes, b => Assert.Equal(0, b));
        }

        [Fact]
        public void APipeThatStallsForSeconds_IsCaughtUpAfterwardsInsteadOfPlayedLate()
        {
            // FFmpeg takes 5 s to start reading. When it does, the stream must jump to "now", not resume from 0.
            var (feeder, pipe, clock) = Make();
            pipe.StallOnceAtOrAfter = TimeSpan.Zero;
            pipe.StallFor = TimeSpan.FromSeconds(5);
            clock.StopAt = TimeSpan.FromSeconds(6);

            feeder.Run(clock.Cts.Token);

            Assert.InRange(pipe.Frames, (long)(5.55 * Rate), 6 * Rate);
        }

        // ------------------------------------------------------ sound goes where it was captured

        [Fact]
        public void SoundIsPlacedWhereItWasCaptured()
        {
            var (feeder, pipe, clock) = Make();
            clock.StopAt = TimeSpan.FromSeconds(2);
            PushAt(clock, feeder, 1.0, marker: 7, frames: Rate / 10);   // 100 ms that ended at t = 1.0 s

            feeder.Run(clock.Cts.Token);

            // So it began at 0.9 s.
            Assert.InRange(FirstFrameOf(pipe, 7), (long)(0.9 * Rate) - 20, (long)(0.9 * Rate) + 20);
            Assert.Equal(Rate / 10, CountOf(pipe, 7));
        }

        [Fact]
        public void SoundKeepsItsPlace_EvenWhenThePipeStallsBeforeItIsWritten()
        {
            // The device delivers sound at t = 2.0 s. The pipe then stalls for a second before the sound can be
            // written. The sound must still sit at 1.9 s in the stream, not at 3.0 s when it finally got through.
            var (feeder, pipe, clock) = Make();
            clock.StopAt = TimeSpan.FromSeconds(4);
            PushAt(clock, feeder, 2.0, marker: 9, frames: Rate / 10);
            pipe.StallOnceAtOrAfter = TimeSpan.FromSeconds(2.0);
            pipe.StallFor = TimeSpan.FromSeconds(1);

            feeder.Run(clock.Cts.Token);

            Assert.InRange(FirstFrameOf(pipe, 9), (long)(1.9 * Rate) - 20, (long)(1.9 * Rate) + 20);
        }

        [Fact]
        public void AChunkThatOverlapsWhatIsAlreadyWritten_HasItsHeadTrimmedSoItsEndStaysPut()
        {
            var (feeder, pipe, clock) = Make();
            clock.StopAt = TimeSpan.FromSeconds(2);
            // 500 ms of sound ending at t = 1.0 s, so it began at 0.5 s. Idle padding has already run to about
            // 0.7 s, so the front of the chunk would be played twice if it were written whole.
            PushAt(clock, feeder, 1.0, marker: 5, frames: Rate / 2);

            feeder.Run(clock.Cts.Token);

            long first = FirstFrameOf(pipe, 5);
            int kept = CountOf(pipe, 5);
            Assert.InRange(kept, 1, Rate / 2 - 1);                     // trimmed, not written whole
            Assert.InRange(first + kept, Rate - 20, Rate + 20);              // and it still ends when it was captured
        }

        [Fact]
        public void SteadyDeviceChunks_AreCopiedWithoutGapsOrDrops()
        {
            var (feeder, pipe, clock) = Make();
            clock.StopAt = TimeSpan.FromSeconds(2);
            int pushed = 0;
            double next = 0.5;
            var rng = new Random(1);
            clock.OnSleep = now =>
            {
                // Ten-millisecond chunks arriving with a few ms of jitter, like a real device.
                while (now.TotalSeconds >= next + rng.NextDouble() * 0.003 && next < 1.5)
                {
                    feeder.Push(Chunk(3, Rate / 100));
                    pushed++;
                    next += 0.01;
                }
            };

            feeder.Run(clock.Cts.Token);

            Assert.True(pushed >= 90);
            Assert.Equal(pushed * (Rate / 100), CountOf(pipe, 3));   // nothing dropped

            // No silence punched into the middle of the run.
            var bytes = pipe.Bytes;
            int first = Array.IndexOf(bytes, (byte)3);
            int last = Array.LastIndexOf(bytes, (byte)3);
            int zerosInside = bytes.Skip(first).Take(last - first + 1).Count(b => b == 0);
            Assert.InRange(zerosInside / BytesPerFrame, 0, Rate / 50);   // at most a couple of frames' worth of slack
        }

        /// <summary>
        /// Real devices hand over contiguous sound in lumps whose delivery time wanders by tens of ms, especially
        /// while a game is loading the CPU. That wander must not be treated as gaps and overlaps in the sound:
        /// doing so cut a hole of a few tens of ms every ~200 ms, heard as the audio "bobbing in and out".
        /// </summary>
        [Theory]
        [InlineData(1, 0.08)]
        [InlineData(2, 0.08)]
        [InlineData(3, 0.15)]
        public void SoundThatArrivesInLumpsWithWanderingDelivery_IsWrittenWithoutHolesOrDrops(int seed, double maxJitterSeconds)
        {
            var (feeder, pipe, clock) = Make();
            clock.StopAt = TimeSpan.FromSeconds(4);

            // 100 ms lumps of continuous sound; lump k really covers [0.5 + 0.1k, 0.6 + 0.1k], delivered late by a random amount.
            var rng = new Random(seed);
            var arrivals = new List<double>();
            double lastArrival = 0;
            for (int k = 0; k < 30; k++)
            {
                double at = Math.Max(0.6 + 0.1 * k + rng.NextDouble() * maxJitterSeconds, lastArrival + 0.001);
                arrivals.Add(at);
                lastArrival = at;
            }

            int delivered = 0;
            clock.OnSleep = now =>
            {
                while (delivered < arrivals.Count && now.TotalSeconds >= arrivals[delivered])
                {
                    feeder.Push(Chunk(3, Rate / 10));
                    delivered++;
                }
            };

            feeder.Run(clock.Cts.Token);

            Assert.Equal(30, delivered);
            Assert.Equal(30 * (Rate / 10), CountOf(pipe, 3));   // every frame of sound made it in

            var bytes = pipe.Bytes;
            int first = Array.IndexOf(bytes, (byte)3);
            int last = Array.LastIndexOf(bytes, (byte)3);
            int zerosInside = bytes.Skip(first).Take(last - first + 1).Count(b => b == 0) / BytesPerFrame;
            Assert.InRange(zerosInside, 0, Rate / 100);   // and no holes punched between the lumps (10 ms of slack)
        }

        [Fact]
        public void ADelay_PushesAllSoundLater()
        {
            var plain = Make();
            plain.Clock.StopAt = TimeSpan.FromSeconds(2);
            PushAt(plain.Clock, plain.Feeder, 1.0, marker: 7, frames: Rate / 10);
            plain.Feeder.Run(plain.Clock.Cts.Token);

            var delayed = Make(delay: TimeSpan.FromMilliseconds(100));
            delayed.Clock.StopAt = TimeSpan.FromSeconds(2);
            PushAt(delayed.Clock, delayed.Feeder, 1.0, marker: 7, frames: Rate / 10);
            delayed.Feeder.Run(delayed.Clock.Cts.Token);

            long shift = FirstFrameOf(delayed.Pipe, 7) - FirstFrameOf(plain.Pipe, 7);
            Assert.InRange(shift, 4800 - 30, 4800 + 30);
        }

        // ------------------------------------------------------------------- bookkeeping

        [Fact]
        public void ItReportsWhenFfmpegHasStartedReading()
        {
            var (feeder, pipe, clock) = Make();
            pipe.StallOnceAtOrAfter = TimeSpan.Zero;
            pipe.StallFor = TimeSpan.FromSeconds(1);
            clock.StopAt = TimeSpan.FromSeconds(1.1);

            Assert.False(feeder.Consuming);
            feeder.Run(clock.Cts.Token);

            Assert.True(feeder.Consuming);
        }

        [Fact]
        public void ItReportsTheFramesItHasWritten()
        {
            var (feeder, pipe, clock) = Make();
            clock.StopAt = TimeSpan.FromSeconds(1);

            feeder.Run(clock.Cts.Token);

            Assert.Equal(pipe.Frames, feeder.FramesWritten);
        }

        [Fact]
        public void APipeThatClosesImmediately_EndsQuietly()
        {
            var (feeder, pipe, clock) = Make();
            pipe.Broken = true;

            Assert.Null(Record.Exception(() => feeder.Run(clock.Cts.Token)));
        }

        [Fact]
        public void APipeThatClosesLater_EndsQuietly()
        {
            var (feeder, pipe, clock) = Make();
            clock.StopAt = TimeSpan.FromSeconds(5);
            clock.OnSleep = now => { if (now.TotalSeconds > 1) pipe.Broken = true; };

            Assert.Null(Record.Exception(() => feeder.Run(clock.Cts.Token)));
        }

        [Fact]
        public void TooMuchQueuedSound_KeepsOnlyTheNewest()
        {
            var (feeder, pipe, clock) = Make();
            // The pipe stalls, so sound piles up behind it. Only the last couple of seconds are worth keeping.
            pipe.StallOnceAtOrAfter = TimeSpan.Zero;
            pipe.StallFor = TimeSpan.FromSeconds(10);
            clock.StopAt = TimeSpan.FromSeconds(11);
            for (int i = 0; i < 6; i++)
            {
                double t = 0.5 + i * 1.5;
                byte marker = (byte)(20 + i);
                var done = false;
                var previous = clock.OnSleep;
                clock.OnSleep = now =>
                {
                    previous?.Invoke(now);
                    if (!done && now.TotalSeconds >= t) { done = true; feeder.Push(Chunk(marker, Rate)); }
                };
            }

            // Everything is pushed while the first write is stalled (virtual time jumps 10 s at once), so this
            // exercises the cap on queued sound rather than real timing.
            feeder.Run(clock.Cts.Token);

            Assert.Equal(0, CountOf(pipe, 20));   // the oldest were dropped
            Assert.True(feeder.PendingMilliseconds >= 0);
        }
    }
}
