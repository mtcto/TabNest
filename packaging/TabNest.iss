; TabNest 安装脚本（Inno Setup 6）
;
; 构建方式：
;   1. dotnet publish src/TabNest.App -c Release -r win-x64 -o publish/v1 ^
;        --self-contained true -p:PublishTrimmed=true -p:PublishSingleFile=true ^
;        -p:EnableCompressionInSingleFile=true
;   2. iscc packaging/TabNest.iss
;
; 产物是单个 exe，因此安装包只需分发一个文件 —— 不需要 .NET 运行时，
; 不需要 VC++ 运行库，用户双击装完即用。

#define AppName "TabNest"
#define AppVersion "0.1.0"
#define AppPublisher "TabNest Contributors"
#define AppExeName "TabNest.exe"

[Setup]
AppId={{8F3A2C41-6B7D-4E52-9A18-2D5C7E9B4F03}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=..\publish\installer
OutputBaseFilename=TabNest-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; 安装程序自身的图标，以及「添加或删除程序」列表里显示的图标。
SetupIconFile=..\src\TabNest.App\TabNest.ico
UninstallDisplayIcon={app}\{#AppExeName}

; 未签名说明。官方发布不签名，用户需自行决定是否继续。
InfoBeforeFile=unsigned-notice.txt

; 不要求管理员权限。
;
; TabNest 只操作当前用户会话里的窗口，装到用户目录即可，这样也避免了
; 提权后与普通权限应用之间的完整性级别不匹配 —— 高完整性进程无法操作
; 低完整性窗口，反过来也一样，装成管理员反而会让部分应用无法分组。
PrivilegesRequired=lowest

; 单文件发布产物只支持 x64/ARM64。
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64

[Languages]
Name: "chinese"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "登录 Windows 后自动启动 TabNest"; GroupDescription: "启动选项"

[Files]
Source: "..\publish\v1\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"

; 安装向导与「添加或删除程序」里的图标。
; 不指定的话前者是 Inno 的默认图标、后者是空白，与产品本身对不上。

[Registry]
; 开机自启。写 HKCU 而非 HKLM —— 这是每个用户自己的选择。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "TabNest"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即运行 {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前先让正在运行的实例正常退出。
;
; 这一步不能省：TabNest 退出时会把所有被接管的窗口还原到原始位置并恢复
; 任务栏按钮。直接删文件而不让它走退出流程，用户的窗口会永久停在被接管的
; 位置上，且任务栏按钮再也回不来 —— 卸载一个窗口管理工具却把窗口弄乱，
; 是最不可接受的收尾。
Filename: "taskkill"; Parameters: "/IM {#AppExeName}"; Flags: runhidden; RunOnceId: "StopTabNest"

[UninstallDelete]
; 日志属于诊断产物，卸载时清理。
; **配置与已保存分组刻意保留** —— 它们是用户资产，重装后应当还在。
; 用户若真想彻底清除，数据目录的路径写在 README 里。
Type: filesandordirs; Name: "{localappdata}\TabNest\logs"
