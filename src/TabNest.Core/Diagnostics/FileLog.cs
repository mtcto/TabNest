using System.Collections.Concurrent;
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
/// 极简滚动文件日志，**写入完全不阻塞调用方**。
///
/// 刻意不引入 Serilog 一类的库：常驻进程的依赖面越小越好，而我们需要的功能
/// 只有"把一行文本安全地追加到文件"。
///
/// 早期版本直接在调用线程上 File.AppendAllText 并每次检查文件大小。
/// 结果是拖动窗口时每帧一次同步写盘发生在 UI 线程上，日志文件越大越慢，
/// 再叠加杀软对持续追加文件的扫描，实测能把拖动卡住两秒 ——
/// **诊断工具本身成了性能问题的根源**。现在改为投递到后台线程写入。
///
/// **绝不记录窗口内容、文档内容或用户输入**（产品原则五：本地优先）。
/// 只记录窗口标题、进程名这类定位问题必需的元数据，且日志永不自动上传。
/// </summary>
public static class FileLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int KeepFiles = 3;

    /// <summary>队列上限。日志爆量时宁可丢弃，也不能让写日志拖慢产品。</summary>
    private const int QueueCapacity = 4096;

    private static readonly BlockingCollection<string> Queue =
        new(new ConcurrentQueue<string>(), QueueCapacity);

    private static readonly Lock Gate = new();
    private static Thread? _writer;
    private static string? _path;
    private static LogLevel _minimum = LogLevel.Info;
    private static long _dropped;

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

                _writer = new Thread(WriteLoop)
                {
                    Name = "TabNest.FileLog",
                    IsBackground = true,

                    // 日志写入优先级低于一切。它绝不该和 UI 抢 CPU。
                    Priority = ThreadPriority.BelowNormal,
                };

                _writer.Start();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写不了不该阻止程序运行。
                _path = null;
            }
        }
    }

    /// <summary>因队列已满被丢弃的日志条数。持续增长说明日志量过大。</summary>
    public static long DroppedEntries => Interlocked.Read(ref _dropped);

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

        // 只投递，不落盘。调用方（可能是 UI 线程）绝不能在这里等待磁盘。
        // 队列满时直接丢弃：日志是诊断手段，不该反过来拖慢被诊断的产品。
        if (!Queue.TryAdd(line.ToString()))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>后台写入循环。批量取出，减少系统调用与文件大小检查的次数。</summary>
    private static void WriteLoop()
    {
        var batch = new StringBuilder(4096);

        try
        {
            foreach (var entry in Queue.GetConsumingEnumerable())
            {
                batch.Clear();
                batch.Append(entry);

                // 把此刻已排队的其余条目一并取走，一次写盘搞定。
                while (batch.Length < 32 * 1024 && Queue.TryTake(out var more))
                {
                    batch.Append(more);
                }

                Flush(batch.ToString());
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 队列关闭，正常退出。
        }
    }

    private static void Flush(string text)
    {
        var path = _path;
        if (path is null)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                File.AppendAllText(path, text, Encoding.UTF8);
                Roll();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 静默忽略：磁盘问题不该影响产品运行。
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
