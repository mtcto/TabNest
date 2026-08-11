# Groupy 2 架构取证报告

> 采集日期：2026-08-11
> 目标：Stardock Groupy 2 **v2.3.1**（`GroupyCtrl.exe` 2.3.1.0 / `GroupyConfig.exe` 2.3.1.1）
> 环境：Windows 11 10.0.26100 x64，24 逻辑核
> 用途：为 TabNest 的技术选型、功能对齐与性能目标提供事实依据

## 取证方法与边界

| 手段 | 说明 |
|---|---|
| 文件枚举 | `Get-ChildItem` 遍历安装目录 |
| PE 托管性判定 | `[Reflection.AssemblyName]::GetAssemblyName()` 成功即为 .NET 程序集 |
| 运行时模块表 | `Process.Modules` 读取已加载 DLL |
| 导入表扫描 | 读取 PE 字节流，ASCII 匹配导入函数名与模块名 |
| 语言资源解析 | `lang/en-us.lng`（UTF-16LE，INI 格式）提取全部 331 条 UI 字符串 |
| 配置读取 | `HKCU:\Software\Stardock\Groupy\Groupy.ini\Groupy` |
| 界面核对 | 逐页浏览设置界面并截图 |
| 性能采样 | `TotalProcessorTime` 差分，20 秒空闲窗口 |

**未做**：反汇编、调试器附加、进程注入、内存读取、网络抓包。
**未改动**：Groupy 的任何设置、配置文件或注册表项。界面操作仅限导航与折叠面板展开。

导入表扫描是字符串匹配，只能证明**存在**某个符号，不能证明其调用路径与频率；"未命中"的判定强度低于"命中"（可能经序号导入或动态解析）。下文对 `SetParent` 的结论已结合 WindowBlinds 依赖交叉验证。

---

## 一、语言与 UI 框架

全部核心 PE 判定为 **NATIVE**，无任何 .NET 程序集：

```
GroupyCtrl.exe        NATIVE   2.3.1.0
GroupyConfig.exe      NATIVE   2.3.1.1
GroupyCore.exe        NATIVE   2.1.5.0
Groupy_64.dll         NATIVE   2.3.1.0
GroupyHelp64.exe      NATIVE   1.4.0.0
GroupySrv.exe         NATIVE   2.1.5.0
SdAppServices_x64.dll NATIVE   1.9.3.2353
```

`GroupyConfig` 运行时加载的 UI 相关模块仅有 `COMCTL32` / `gdiplus` / `UxTheme`。

`GroupyCtrl` 渲染 API 命中情况：

| 命中 | 未命中 |
|---|---|
| `GdipDrawImage`、`GdipCreateBitmap`、`AlphaBlend`、`CreateCompatibleDC`、`SetWindowRgn`、`CreateWindowExW`、`RegisterClassExW`、`DwmExtendFrameIntoClientArea`、`TrackMouseEvent` | `D2D1CreateFactory`、`DCompositionCreate`、`GetThemePartSize` |

**结论**：Groupy 2 是原生 C++ + **GDI+ 自绘**，皮肤资源在 `Default.spak`。既非 WPF，也非 WinUI 3，也未使用 Direct2D 或 DirectComposition。

> 对 TabNest 的意义：标签栏本身是很轻的自绘控件，UI 框架不构成瓶颈。但 GDI+ 的位图皮肤在高 DPI 下会被拉伸，TabNest 用 Direct2D 矢量渲染在 150% 缩放下有直接的清晰度优势。

---

## 二、核心机制：全局 DLL 注入

安装目录含三份架构专用 hook DLL 与三个桥接助手进程：

```
Groupy_32.dll  996 KB      GroupyHelp32.exe
Groupy_64.dll  1186 KB     GroupyHelp64.exe
Groupy_A64.dll 1194 KB     GroupyHelpA64.exe
```

实测遍历 309 个可读进程，**46 个已加载 Groupy hook DLL**，覆盖面近乎全量：

```
chrome, idea64, explorer, conhost, OpenConsole, msedgewebview2, ChatGPT,
OneDrive, powershell, pwsh, ApplicationFrameHost, cef_server, clash-verge,
localsend_app, M365Copilot, OpenVPNConnect, RadeonSoftware, AMDRSServ, ...
```

连 Groupy 自己的设置进程 `GroupyConfig` 也被注入了 `groupy_32.dll`。

`Groupy_64.dll` 导入模块：

| 模块 | 作用 |
|---|---|
| `uxhook.dll` | Stardock 自研注入引擎，`SetWindowsHookEx` 在此（Groupy 自身未直接导入） |
| `wblind.dll` / `wblind64.dll` / `wbhelp.dll` | **WindowBlinds 皮肤引擎** —— 标签绘制进标题栏靠它 |
| `twinui.pcshell.dll` | Windows 未文档化 shell 内部接口（虚拟桌面 / 任务视图） |
| `xshell32.dll` | Stardock shell 辅助 |
| `steam_api.dll` | Steam 渠道授权 |
| `user32` / `gdi32` / `dwmapi` / `shell32` / `advapi32` / `ole32` | 标准系统依赖 |

`Groupy_64.dll` 自身命中 `CallNextHookEx`，证实其为 hook 过程 DLL。

---

## 三、关键 API 使用

| API | 命中 | 含义 |
|---|:---:|---|
| `SetParent` | ✗ | **Groupy 不通过父子关系重建实现"嵌入"** |
| `SetWindowsHookEx` | ✗ | 委托给 `uxhook.dll` |
| `SetWinEventHook` | ✗ | 无需——代码已在目标进程内，直接收窗口消息 |
| `CallNextHookEx` | ✓ | 本体是 hook 过程 |
| `SetWindowLongPtrW` / `GetWindowLongPtrW` | ✓ | 修改窗口样式 |
| `SetWindowPos` / `ShowWindow` | ✓ | 窗口重排 |
| `Get/SetWindowPlacement` | ✓ | 状态快照与恢复 |
| `SetForegroundWindow` | ✓ | 焦点转移 |
| `AllowSetForegroundWindow` | ✓ | 授予前台权限 |
| `SwitchToThisWindow` | ✓ | 焦点转移兜底 |
| `AttachThreadInput` | ✗ | **不需要**——见下方分析 |
| `SetPropW` / `GetPropW` | ✓ | 每窗口状态挂在外部 HWND 上 |
| `SendMessageTimeoutW` | ✓ | 跨进程同步消息带超时 |
| `IsHungAppWindow` | ✓ | 操作前检测目标是否假死 |
| `Dwm(Get\|Set)WindowAttribute` | ✓ | 标题栏与边框属性 |
| `SetLayeredWindowAttributes` | ✓ | 分层窗口 |

### 3.1 Groupy 不用 SetParent

导入表无 `SetParent`，且标签绘制依赖 WindowBlinds 皮肤引擎。综合判断：Groupy 的"集成标签"是**在目标进程内接管标题栏的非客户区绘制**，配合进程外的窗口重排，而不是把目标窗口变成宿主窗口的子窗口。

> **这推翻了 TabNest 计划书 §6.4 的核心假设**。计划书把"像 Groupy 一样嵌入"等同于"`SetParent` 承载"，该等式不成立。照 §6.4 实现只会走上一条 Groupy 从未走过、且更脆弱的路。

### 3.2 焦点转移：Groupy 结构性占优

Groupy 用 `AllowSetForegroundWindow` + `SwitchToThisWindow`，**没有** `AttachThreadInput`。原因是它的代码运行在目标进程内部，天然持有前台窗口权限，不受 Windows 前台锁定限制。

TabNest 阶段一在进程外，`SetForegroundWindow` 会被前台锁定拦下（表现为任务栏图标闪烁而非真正激活）。因此**必须**实现降级链：

```
SetForegroundWindow
  → AttachThreadInput 附加前台线程后重试
  → SetWindowPos(HWND_TOP)
  → SwitchToThisWindow
```

每级后校验 `GetForegroundWindow`。这是 TabNest 相对 Groupy 结构性更难的一点，也是计划书 §11.1「焦点落到 TabNest」风险的真正根因。

### 3.3 值得直接借鉴

- **`SendMessageTimeoutW` + `IsHungAppWindow`**：所有跨进程同步消息带超时，操作前预检目标是否假死。计划书列了"不响应窗口"风险但未给手段。
- **`SetPropW`/`GetPropW`**：把每窗口状态挂在外部 HWND 上。TabNest 阶段一不采用（进程外无必要且属侵入），记录备阶段二参考。

---

## 四、性能基准

20 秒空闲采样（不操作鼠标键盘），24 逻辑核：

| 进程 | 工作集 | 私有内存 | 线程 | 句柄 | CPU (20s) | 单核占用 |
|---|---:|---:|---:|---:|---:|---:|
| GroupyCtrl | 73.2 MB | 94.4 MB | 25 | 367 | 16 ms | 0.08% |
| GroupySrv | 1.2 MB | 1.0 MB | 3 | 103 | 0 ms | 0 |
| GroupyHelp32 | 2.0 MB | 1.2 MB | 1 | 127 | 0 ms | 0 |
| GroupyHelp64 | 1.6 MB | 1.2 MB | 1 | 109 | 0 ms | 0 |
| **常驻合计** | **78 MB** | — | **30** | **706** | **16 ms** | **0.08%**（全系统 0.003%） |

（`GroupyConfig` 两个实例 25.9 + 19.0 MB 未计入常驻——设置窗口非常驻。）

**空闲 CPU 几乎为零**，因为 hook 是被动的：窗口事件在目标进程内直接投递给 hook 过程，无任何轮询。TabNest 用 `SetWinEventHook` OUTOFCONTEXT 同样是被动投递，理论上可达到同一量级，前提是事件合并得当且不引入任何定时轮询。

**未计入的隐性成本**：hook DLL 被映射进 46 个宿主进程，每个进程承担 DLL 镜像与 hook 分发开销，不出现在上表任何一行。TabNest 阶段一注入数为 0，系统级总开销结构性更低——这是应当明确宣传的优势。

---

## 五、完整功能面（331 条 UI 字符串 + 界面核对）

设置中心为 Windows 11 Fluent 浅色风格，左侧导航 + 右侧卡片式内容，图示化单选卡片 + 开关行 + 折叠面板。**无暗色主题，无设置项搜索。**

### 5.1 总览

1. 选择标签类型：将标签页在上方 / 集成标签页（图示卡片，含"仅当窗口未使用自定义标题栏时才能集成"警告）
2. 鼠标悬停未分组窗口标题栏顶部时显示 Groupy 条（开关）
3. 分组窗口的任务栏按钮：显示全部 / 仅显示活动窗口 / 仅显示一个组合按钮（带自定义图标）
4. 禁用 Groupy（主开关，按账户）
5. 服务未运行时的状态检测与修复引导

### 5.2 标签外观

标签样式（传统 / 圆形）、添加背景条、加高标签与背景栏、可见性四策略（始终可见 / 在活动窗口显示 / 最大化时隐藏 / 始终隐藏）。

下钻「高级选项卡设置」：关闭按钮四策略（悬停时 / 仅活动标签 / 所有标签 / 从不）+ 11 个开关——单标签时不绘制、显示窗口图标、可变长度标签、隐藏标题文本、悬停预览折叠、显示关闭所有按钮、隐藏 Groupy 菜单按钮、直角标签、不显示下拉箭头、不做动画、不画集成标签栏下划线。

### 5.3 标签颜色

主题色管理（新建 / 重命名 / 删除）、专注与未聚焦标签色、专注背景条（Mica 风格）、背景模糊条、各自重置默认、应用到记事本与资源管理器集成标签、应用到 Office 标签、强制明暗模式。

**五级颜色作用域**：按应用 / 按文件夹及子文件夹 / 按文档 / 按标题文本 / 按组。

### 5.4 分组设置

Explorer 与记事本原生标签处理（替换 / 不替换）+ 五个折叠区：

- **关闭分组**：集成标签的关闭按钮关闭整组、Explorer/记事本关闭按钮关闭整组、Ctrl 仅关当前、重开已关闭标签
- **手动拖放分组**：触发方式四选一（总是 / Shift / Ctrl / 右键按住）、延迟四选一（立即 / ⅓ / ⅔ / 1 秒）、仅同应用可拖放分组（除非按 Shift）、整窗口作为拖放目标、标签移动需按 Shift 或右键
- **移动和新建标签**：Ctrl+T / Ctrl+N 新建标签、按应用排除 Ctrl-N/T、中键行为三选一（无 / 关闭标签 / 新建标签）、Word/Excel 新建行为、新建标签按钮打开当前应用副本
- **热键设置**：Win + `（Ctrl 反向）、Ctrl+Tab / Shift+Tab 组内切换、重命名标签热键
- **探索增强设置**：中键调用选中项与向上导航、中键新文件夹加入当前组、新 Explorer 标签在同文件夹打开

### 5.5 分组规则

- **自动窗口分组**：自动合并同类窗口（除非已在其他组）、按住 Ctrl 打开新窗口时分组、查看与删除自动分组条目
- **已保存分组**：保存、双击启动、开始菜单文件夹、固定到任务栏 / 开始菜单
- **阻止/允许应用**：包含"仅允许指定应用在组中"模式
- **自定义规则**：进程 + 窗口类 + 标题文本组合匹配、Office 文件与 Explorer 文件夹匹配、部分字符串匹配、子规则、允许空规则匹配全部、规则命名

### 5.6 上下文菜单（约 25 项）

**批量收编**：某应用全部未分组窗口 / 该应用本显示器的 / 某窗口类的 / 本显示器全部。
**关闭语义**：关闭标签 / 关闭其他 / 关闭左侧 / 关闭右侧 / 关闭组内全部窗口。
**其他**：解散组、锁定与解锁组、固定与取消固定标签、重命名标签、保存组与另存为、个性化标签色、强制明暗模式、自动分组此类窗口、自动排除此类窗口、从 Groupy 排除此应用、始终为此类窗口显示标签、排除 Ctrl-N/T 支持。

### 5.7 前置条件与错误状态

- 「Groupy 需要启用桌面窗口管理器」
- 「需要在 Windows 中启用拖动时显示窗口内容」
- 「无法分组未响应的窗口」
- 「标签只能集成进未使用自定义标题栏的窗口」
- 「管理员已锁定 Groupy 设置，是否以管理员身份运行以修改？」（企业策略锁定）
- 首次运行专门弹窗确认是否保留延迟分组

---

## 六、配置存储

`HKCU:\Software\Stardock\Groupy\Groupy.ini\Groupy`，子键 `AddTab` / `AlwaysShowTabs` / `AutoGrouping` / `Exclusions` / `SavedColours`。

本机实测**仅存 9 个值**：

```
AlsoRegisterRenameKey=0   AskedAboutDelayGrouping=1   ExplorerMiddleButton=0
GroupyGroupMode=0         GroupyHotKey=1              InclusionListMode=1
NewInstallDone=1          SetupDefaultRules=1         SetupDefaultRules2=1
```

**仅非默认值落盘**。配置极小，schema 迁移简单，用户改过什么一目了然。

> TabNest 的 `AtomicJsonStore` 采用同样的稀疏写入策略。

---

## 七、对 TabNest 的结论

| # | 结论 | 影响 |
|---|---|---|
| 1 | Groupy 不用 `SetParent` | 计划书 §6.4 方案 A 的技术描述作废 |
| 2 | 集成标签必须进程内渲染 | 在不注入前提下物理不可达；计划书 §8.4 将其列为 P0 是错的 |
| 3 | 焦点转移 Groupy 结构性占优 | TabNest 进程外必须实现四级降级链 |
| 4 | 任务栏按钮策略可外部实现 | `ITaskbarList::DeleteTab`/`AddTab` 对任意进程 HWND 有效且可逆；计划书 §6.5 与 §11.3 把它误记为方案 B 的先天缺陷 |
| 5 | 常驻 78 MB / 0.08% CPU | 计划书的 180 MB / 1% 目标相对竞品是明显倒退，已全面重设 |
| 6 | 悬停浮出条是日常分组主入口 | 计划书完全遗漏，只有欢迎页引导。缺此功能产品不可用 |
| 7 | 批量收编菜单 | 计划书遗漏，效率比逐个拖拽高一个量级 |
| 8 | 无暗色主题、无设置搜索 | TabNest 的明确超越点 |
| 9 | 稀疏配置写入 | 直接借鉴 |
| 10 | 46 进程注入的隐性开销 | TabNest 阶段一为 0，应作为差异化优势宣传 |
