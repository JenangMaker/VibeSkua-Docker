using System.Text;

namespace Skua.App.Avalonia;

/// <summary>
/// Windows: VibeSkua is a windowed program, so what it writes to the console (its
/// own lines and, forwarded, each tab's and each game player's) went nowhere: a
/// windowed program has no console, and a terminal does not capture it either.
/// It now also goes to %APPDATA%\vibeskua\log\NAME.log, one timestamp a line. The
/// previous run's file is kept as NAME.1.log; a file over 20 MB is moved there too
/// and started again. Tabs need none of their own: the tab host forwards their
/// lines (tagged "[tab N]") to its own console.
/// </summary>
internal static class LogFile
{
    private const long MaxBytes = 20L * 1024 * 1024;

    public static void Install(string name)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vibeskua", "log");
            Directory.CreateDirectory(dir);
            var sink = new Sink(Path.Combine(dir, name + ".log"));
            Console.SetOut(new Tee(Console.Out, sink));
            Console.SetError(new Tee(Console.Error, sink));
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                sink.WriteLine($"[host] unhandled exception: {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) =>
                sink.WriteLine($"[host] unobserved task exception: {e.Exception}");
            string version = typeof(LogFile).Assembly.GetName().Version?.ToString() ?? "?";
            sink.WriteLine($"[host] VibeSkua {version} started, pid {Environment.ProcessId}, args: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[host] no log file: {e.Message}");
        }
    }

    /// <summary>The file, shared by stdout and stderr; whole lines only.</summary>
    private sealed class Sink
    {
        private readonly string _path;
        private readonly object _lock = new();
        private StreamWriter _writer;
        private long _size;

        public Sink(string path)
        {
            _path = path;
            MoveAway();
            _writer = Open();
        }

        public void WriteLine(string line)
        {
            lock (_lock)
            {
                try
                {
                    string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}";
                    _writer.Write(text);
                    _size += text.Length;
                    if (_size > MaxBytes)
                    {
                        _writer.Dispose();
                        MoveAway();
                        _writer = Open();
                    }
                }
                catch
                {
                    // A full disk or a lost file must not stop the program.
                }
            }
        }

        // The last run's (or the full) file becomes NAME.1.log. Another VibeSkua
        // with it open (two copies running): then this one appends to it.
        private void MoveAway()
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > 0)
                    File.Move(_path, Path.ChangeExtension(_path, ".1.log"), overwrite: true);
            }
            catch (IOException)
            {
            }
        }

        private StreamWriter Open()
        {
            var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _size = stream.Length;
            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
    }

    /// <summary>Writes through to the original writer and, a line at a time, to the file.</summary>
    private sealed class Tee(TextWriter inner, Sink sink) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            if (value == '\n')
            {
                sink.WriteLine(_line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
            else
            {
                _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;
            inner.Write(value);
            int start = 0;
            for (int nl; (nl = value.IndexOf('\n', start)) >= 0; start = nl + 1)
            {
                _line.Append(value, start, nl - start);
                sink.WriteLine(_line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
            _line.Append(value, start, value.Length - start);
        }

        public override void WriteLine(string? value) => Write((value ?? "") + "\n");

        public override void Flush() => inner.Flush();
    }
}
