using System;
using System.IO;
using System.Text;

namespace ChronoRecorder
{
    /// <summary>
    /// Copies everything written to the console into a log file. The installed app has no console window, so this is
    /// the only way to see what happened when something goes wrong on a friend's PC.
    /// </summary>
    public static class FileLog
    {
        private const long RotateAtBytes = 2 * 1024 * 1024;

        public static string DefaultFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chrono", "logs");

        public static string? CurrentPath { get; private set; }

        public static void Attach(string? folder = null)
        {
            try
            {
                folder ??= DefaultFolder;
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "chrono.log");

                // One previous log is kept, so a crash right after a restart is still readable.
                if (File.Exists(path) && new FileInfo(path).Length > RotateAtBytes)
                    File.Move(path, Path.Combine(folder, "chrono.previous.log"), overwrite: true);

                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(new TeeWriter(Console.Out, file));
                CurrentPath = path;
                Console.WriteLine($"\n===== Chrono started {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Couldn't open the log file: {ex.Message}");   // logging must never stop the app
            }
        }

        private sealed class TeeWriter : TextWriter
        {
            private readonly TextWriter first, second;
            public TeeWriter(TextWriter first, TextWriter second) { this.first = first; this.second = second; }
            public override Encoding Encoding => first.Encoding;

            public override void Write(char value) { Try(() => first.Write(value)); Try(() => second.Write(value)); }
            public override void Write(string? value) { Try(() => first.Write(value)); Try(() => second.Write(value)); }
            public override void WriteLine(string? value) { Try(() => first.WriteLine(value)); Try(() => second.WriteLine(value)); }
            public override void Flush() { Try(first.Flush); Try(second.Flush); }

            private static void Try(Action action) { try { action(); } catch { } }
        }
    }
}
