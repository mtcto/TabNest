using TabNest.Core.Models;

namespace TabNest.Core.Persistence;

/// <summary>一个成员窗口在快照中的记录。</summary>
public sealed record SessionMember
{
    public required nint Handle { get; init; }
    public required int ProcessId { get; init; }
    public required long ProcessStartTicks { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public required WindowSnapshot Snapshot { get; init; }

    public WindowIdentity ToIdentity() => new(Handle, ProcessId, ProcessStartTicks);
}

/// <summary>一个组在快照中的记录。</summary>
public sealed record SessionGroup
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public uint? AccentColor { get; init; }
    public bool IsLocked { get; init; }
    public required IReadOnlyList<SessionMember> Members { get; init; }
}

/// <summary>
/// 组会话快照。
///
/// 用途只有一个：**在 TabNest 异常终止后把外部窗口还原回原位**。
///
/// <see cref="IsDirty"/> 是关键：正常退出时会写入 false，异常终止则它保持 true。
/// 下次启动读到 true 就知道上次没有走完退出流程，外部窗口很可能还停在被接管的位置上，
/// 应当据此还原并告知用户。
/// </summary>
public sealed record SessionSnapshot
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>写入时是否仍有未还原的窗口。正常退出前会被置为 false。</summary>
    public bool IsDirty { get; init; }

    public DateTimeOffset SavedAt { get; init; }

    public IReadOnlyList<SessionGroup> Groups { get; init; } = [];

    public static SessionSnapshot Empty { get; } = new()
    {
        IsDirty = false,
        SavedAt = default,
        Groups = [],
    };

    /// <summary>快照里是否有需要还原的窗口。</summary>
    public bool NeedsRecovery => IsDirty && Groups.Count > 0;

    public static SessionSnapshot Capture(IEnumerable<GroupSession> groups, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var list = new List<SessionGroup>();

        foreach (var group in groups)
        {
            var members = new List<SessionMember>();

            foreach (var tab in group.LiveTabs)
            {
                members.Add(new SessionMember
                {
                    Handle = tab.Identity.Handle,
                    ProcessId = tab.Identity.ProcessId,
                    ProcessStartTicks = tab.Identity.ProcessStartTicks,
                    Title = tab.Title,
                    ProcessName = tab.ProcessName,
                    Snapshot = tab.Snapshot,
                });
            }

            if (members.Count > 0)
            {
                list.Add(new SessionGroup
                {
                    Id = group.Id,
                    Name = group.Name,
                    AccentColor = group.AccentColor,
                    IsLocked = group.IsLocked,
                    Members = members,
                });
            }
        }

        return new SessionSnapshot
        {
            IsDirty = list.Count > 0,
            SavedAt = now,
            Groups = list,
        };
    }
}
