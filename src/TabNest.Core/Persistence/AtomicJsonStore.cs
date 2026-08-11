using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabNest.Core.Persistence;

/// <summary>读取结果。</summary>
/// <param name="Value">读到的值，或默认值。</param>
/// <param name="Outcome">读取过程中发生了什么。</param>
/// <param name="Error">失败详情，供诊断日志使用。</param>
public readonly record struct LoadResult<T>(T Value, LoadOutcome Outcome, string? Error = null);

public enum LoadOutcome
{
    /// <summary>正常读取。</summary>
    Loaded = 0,

    /// <summary>文件不存在，返回默认值。首次运行的正常情况。</summary>
    NotFound,

    /// <summary>主文件损坏，已从备份恢复。</summary>
    RecoveredFromBackup,

    /// <summary>主文件与备份均不可用，已回退到默认值。</summary>
    FellBackToDefaults,
}

/// <summary>
/// 原子写入的 JSON 存储。
///
/// 写入流程：临时文件 → 落盘 → 原子替换（旧文件转为 .bak）。
/// 直接覆写目标文件是不可接受的：进程在写到一半时崩溃会留下截断的 JSON，
/// 用户的全部工作区配置就此丢失。
///
/// 读取流程：主文件 → 损坏则读 .bak → 仍失败则回退默认值，且**绝不删除损坏的文件**，
/// 而是改名保留，让用户和诊断日志还有机会找回内容。
/// </summary>
public sealed class AtomicJsonStore<T>
    where T : class
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly T _defaults;

    public AtomicJsonStore(string path, T defaults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(defaults);

        _path = path;
        _defaults = defaults;
    }

    public string Path => _path;

    public string BackupPath => _path + ".bak";

    /// <summary>损坏文件的隔离位置。保留而非删除，用户可能想手工抢救内容。</summary>
    public string CorruptPath => _path + ".corrupt";

    public LoadResult<T> Load()
    {
        if (!File.Exists(_path))
        {
            // 主文件不在但备份还在，说明上次写入正好崩在替换那一步。
            if (File.Exists(BackupPath) && TryRead(BackupPath, out var fromBackup, out _))
            {
                return new LoadResult<T>(fromBackup!, LoadOutcome.RecoveredFromBackup);
            }

            return new LoadResult<T>(_defaults, LoadOutcome.NotFound);
        }

        if (TryRead(_path, out var value, out var error))
        {
            return new LoadResult<T>(value!, LoadOutcome.Loaded);
        }

        QuarantineCorruptFile();

        if (File.Exists(BackupPath) && TryRead(BackupPath, out var recovered, out _))
        {
            return new LoadResult<T>(recovered!, LoadOutcome.RecoveredFromBackup, error);
        }

        return new LoadResult<T>(_defaults, LoadOutcome.FellBackToDefaults, error);
    }

    /// <summary>
    /// 原子写入。只写出与默认值不同的字段。
    /// 任何一步失败都会抛出，调用方必须把失败视为"配置未保存"而非静默忽略。
    /// </summary>
    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = SparseJson.Serialize(value, _defaults, SerializerOptions);
        var temp = _path + ".tmp";

        // 先确保临时文件完整落盘，再做替换 —— 顺序反了就失去了原子性的意义。
        File.WriteAllText(temp, json);

        if (File.Exists(_path))
        {
            File.Replace(temp, _path, BackupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, _path);
        }
    }

    private static bool TryRead(string path, out T? value, out string? error)
    {
        try
        {
            var json = File.ReadAllText(path);

            // 空文件不是"空配置"，而是写入中断的残骸，必须当作损坏处理。
            if (string.IsNullOrWhiteSpace(json))
            {
                value = null;
                error = "文件为空";
                return false;
            }

            value = JsonSerializer.Deserialize<T>(json, SerializerOptions);
            if (value is null)
            {
                error = "反序列化结果为 null";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            value = null;
            error = ex.Message;
            return false;
        }
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            File.Move(_path, CorruptPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 隔离失败不该阻止启动 —— 下一次 Save 会覆盖它。
        }
    }
}
