using System.Text;

namespace MyFirstMafAgent.Api.Infrastructure.Logging;

/// <summary>
/// 极简文件日志提供器。桌面版（WinExe）没有控制台，用它把启动与数据库日志落盘，
/// 方便排查便携 PostgreSQL / Kestrel 启动问题。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(string directory, string filePrefix, LogLevel minimumLevel = LogLevel.Information)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"{filePrefix}-{DateTime.Now:yyyyMMdd}.log");
        _writer = new StreamWriter(
            new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        _minimumLevel = minimumLevel;
    }

    public string FilePath { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimumLevel;

    internal void Write(string line)
    {
        try
        {
            lock (_gate)
                _writer.WriteLine(line);
        }
        catch (IOException)
        {
            // 日志写入失败不能影响主流程。
        }
        catch (ObjectDisposedException)
        {
            // 应用退出阶段忽略。
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer.Dispose(); }
            catch (IOException) { }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" [").Append(logLevel).Append("] ")
                .Append(categoryName).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null)
            {
                builder.AppendLine();
                builder.Append(exception);
            }

            provider.Write(builder.ToString());
        }
    }
}
