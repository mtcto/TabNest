# TabNest

把散落在 Windows 桌面上的工作窗口，收进可控、可恢复、可键盘操作的标签工作区。

> **状态：早期开发中（0.1.0）**，尚不可用于日常工作。

---

## 这是什么

TabNest 把多个独立的 Windows 窗口组织成一个标签组——像浏览器标签一样切换、拖拽合并、拖出拆分，但对象是 IDE、终端、文件管理器、浏览器这些互不相干的应用窗口。

它管理的是**窗口**，不修改任何应用的内容或文件。

## 项目结构

```
src/TabNest.Core        纯领域层：组状态机、规则引擎、快照、配置。零 Win32，可 100% 单元测试
src/TabNest.Interop     所有 P/Invoke、窗口观察、窗口控制、任务栏控制
src/TabNest.App         常驻托盘进程、编排、标签轨道、按需加载的设置中心
tests/TabNest.Core.Tests    领域层单元测试
tools/TabNest.Harness       窗口行为测试宿主（开发工具，不随产品发布）
```

## 构建与运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)（版本由 `global.json` 锁定）。不需要 Visual Studio。

```bash
dotnet build
```

```bash
dotnet test
```

无 UI 诊断模式，列出当前窗口及其可分组性判定原因：

```bash
dotnet run --project src/TabNest.App -- --diagnose
```

端到端自测，用真实窗口跑完「分组 → 切换 → 拆分 → 还原」闭环并测量切换延迟。
先启动测试宿主，再运行自测：

```bash
dotnet run --project tools/TabNest.Harness -- --spawn normal,normal
```

```bash
dotnet run --project src/TabNest.App -- --selftest
```

## 设计约束

这几条不是风格偏好，是硬性约束，违反了会直接损害产品：

**常驻层不得加载 WPF。** 托盘、窗口观察、标签轨道全部走 Win32 + Direct2D，只有设置中心用 WPF 且按需加载。`PresentationFramework` 是否被加载由 `--benchmark` 自动校验。

**所有 P/Invoke 集中在 `TabNest.Interop.Native`。** 其他项目不得声明 `[LibraryImport]`。

**领域逻辑不碰窗口。** `GroupSessionManager` 是纯状态机，只产出 `WindowAction` 指令，由 `WindowController` 在单一串行队列上执行。这让组逻辑可以在无窗口环境下完整测试。

**任何跨进程同步消息必须带超时。** 统一走 `SendMessageTimeoutW`，操作前用 `IsHungAppWindow` 预检。目标程序假死不得拖垮 TabNest。

**窗口状态必须可还原。** 每次窗口写操作之前先落盘快照。TabNest 崩溃、强杀、卸载都不得留下位置错乱或隐藏的窗口。

## 性能

对标本机实测的 Stardock Groupy 2（v2.3.1）。TabNest 一列为 Debug 构建实测值：

| 指标 | Groupy 2 | 阶段一目标 | **TabNest 实测** |
|---|---:|---:|---:|
| 常驻工作集 | 78 MB | ≤ 60 MB | **25.9 MB** |
| 空闲 CPU（20s 采样） | 0.08% 单核 | ≤ 0.10% | **0.000%** |
| 线程数 | 30 | ≤ 24 | **14** |
| 句柄数 | 706 | ≤ 400 | **277** |
| 标签切换 P95 | 未测 | ≤ 100 ms | **63 ms** |
| 注入宿主进程数 | 46 | 0 | **0** |

空闲 CPU 能压到零，靠的是两点：`WinEvent` 钩子被动投递、全程无轮询；以及位置变化事件只跟踪组成员，
不理会全系统的窗口移动——后者不做过滤时实测会把空闲 CPU 推到 0.5% 单核以上。

Groupy 基准的采集方式与原始数据见 [`docs/competitive-analysis.md`](docs/competitive-analysis.md)。

## 已知限制

**焦点转移在无前台权限时会降级。** Groupy 的代码注入在目标进程内，天然持有前台窗口权限；
TabNest 在进程外，受 Windows 前台锁定机制约束。用户点击标签时我们的进程收到了最后一次输入事件，
按规则即获得前台权限，此时焦点能正常转移；但在没有用户输入的场景（如脚本调用）下，
四级降级链会退到"仅置顶"——窗口可见并置于最前，键盘焦点仍在别处。
这是降级而非失败，`--selftest` 会如实区分这两种结果。

**尚未实现**：集成标签（需阶段二注入层）、悬停浮出分组条、批量收编菜单、右键上下文菜单、
规则编辑界面、工作区保存、全局快捷键、设置中心图形界面（当前通过 `settings.json` 配置）。

## 路线

**阶段一（当前）** 纯 C#，不注入任何进程。覆盖分组、切换、拖拽合并与拆分、规则、工作区、任务栏按钮策略、悬停分组条、批量收编。标签显示为贴在窗口上方的轨道。

**阶段二** 引入原生 C++ 注入层，实现标签集成进目标窗口标题栏等需要进程内渲染的能力。

阶段一与阶段二的领域逻辑完全共用。

## 许可

[MIT](LICENSE)
