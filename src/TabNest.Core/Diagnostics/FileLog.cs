using System.Globalization;
using System.Text;

namespace TabNest.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// 极简滚动文件日志。
///
/// 刻意不引入 Serilog 一类的库：常驻进程的依赖面越小越好，而我们需要的功能
/// 只有"把一行文本安全地追加到文件"。
///
/// **绝不记录窗口内容、文档内容或用户输入**（产品原则五：本地优先）。
/// 只记录窗口标题、进程名这类定位问题必需的元数据，且日志永不自动上传。
/// </summary>
public static class FileLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int KeepFiles = 3;

    private static readonly Lock Gate = new();
    private static string? _path;
    private static LogLevel _minimum = LogLevel.Info;

    public static bool IsEnabled => _path is not null;

    /// <summary>初始化日志。未调用时所有写入都是空操作，因此单元测试无需关心。</summary>
    public static void Initialize(string directory, LogLevel minimum = LogLevel.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "tabnest.log");
                _minimum = minimum;
                Roll();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写不了不该阻止程序运行。
                _path = null;
            }
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);

    public static void Info(string message) => Write(LogLevel.Info, message, null);

    public static void Warn(string message) => Write(LogLevel.Warning, message, null);

    public static void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, message, exception);

    private static void Write(LogLevel level, string message, Exception? exception)
    {
        if (_path is null || level < _minimum)
        {
            return;
        }

        var line = new StringBuilder(160)
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warning => "WRN",
                _ => "ERR",
            })
            .Append("  ")
            .Append(message);

        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        line.AppendLine();

        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, line.ToString(), Encoding.UTF8);
                Roll();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 同上，静默忽略。
            }
        }
    }

    /// <summary>超过大小上限时滚动。保留有限份数，避免长期运行把磁盘写满。</summary>
    private static void Roll()
    {
        if (_path is null)
        {
            return;
        }

        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        for (var i = KeepFiles - 1; i >= 1; i--)
        {
            var from = $"{_path}.{i}";
            var to = $"{_path}.{i + 1}";

            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
        }

        File.Move(_path, $"{_path}.1", overwrite: true);
    }
}
