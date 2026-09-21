namespace ChronoRecorder
{
    public enum AudioSampleFormat
    {
        Float32,
        Pcm16
    }

    /// <summary>One raw audio stream that FFmpeg reads from a named pipe.</summary>
    public sealed record AudioInput(string PipePath, int SampleRate, int Channels, AudioSampleFormat Format)
    {
        /// <summary>How much louder (or quieter) to make it: 1 = as it is, 2.5 = 250%. Applied while mixing, not to the device.</summary>
        public double Gain { get; init; } = 1.0;

        /// <summary>The name FFmpeg's raw-audio demuxer uses for this sample format.</summary>
        public string FfmpegFormat => Format == AudioSampleFormat.Float32 ? "f32le" : "s16le";

        public int BytesPerFrame => Channels * (Format == AudioSampleFormat.Float32 ? 4 : 2);

        /// <summary>
        /// Describes a device format, or null if it isn't one we can hand to FFmpeg unchanged.
        /// Windows' shared-mode mix format is almost always 32-bit float; 16-bit PCM covers most microphones.
        /// </summary>
        public static AudioInput? Create(string pipePath, int sampleRate, int channels, int bitsPerSample, bool isFloat)
        {
            if (sampleRate <= 0 || channels <= 0) return null;

            if (isFloat && bitsPerSample == 32)
                return new AudioInput(pipePath, sampleRate, channels, AudioSampleFormat.Float32);

            if (!isFloat && bitsPerSample == 16)
                return new AudioInput(pipePath, sampleRate, channels, AudioSampleFormat.Pcm16);

            return null;
        }
    }
}
