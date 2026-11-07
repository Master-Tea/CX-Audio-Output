using System.Text;
using Microsoft.Extensions.Logging;

namespace CXAudioOutput;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _sync = new();
    private readonly StreamWriter? _writer;
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(string filePath, LogLevel minimumLevel = LogLevel.Information)
    {
        _minimumLevel = minimumLevel;
        try
        {
            AppPaths.EnsureDirectories();
            RotateIfNeeded(filePath);
            _writer = new StreamWriter(filePath, true, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }
        catch
        {
            _writer = null;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName, _minimumLevel);

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
        }
    }

    internal void Write(string line)
    {
        if (_writer is null) return;
        lock (_sync)
        {
            try
            {
                _writer.WriteLine(line);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    private static void RotateIfNeeded(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length < 2 * 1024 * 1024) return;
            var previous = filePath + ".old";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(filePath, previous);
        }
        catch
        {
            // ignored
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;
        private readonly LogLevel _minimumLevel;

        public FileLogger(FileLoggerProvider provider, string category, LogLevel minimumLevel)
        {
            _provider = provider;
            _category = category;
            _minimumLevel = minimumLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var builder = new StringBuilder(180);
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            builder.Append(" [").Append(LevelText(logLevel)).Append("] ");
            builder.Append(_category).Append(": ").Append(formatter(state, exception));
            if (exception is not null)
            {
                builder.AppendLine();
                builder.Append(exception);
            }

            _provider.Write(builder.ToString());
        }

        private static string LevelText(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }
}
