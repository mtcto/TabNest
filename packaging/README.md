# 打包与发布

## 构建安装包

需要 [Inno Setup 6](https://jrsoftware.org/isdl.php)。

```bash
dotnet publish src/TabNest.App -c Release -r win-x64 -o publish/v1 --self-contained true -p:PublishTrimmed=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

```bash
iscc packaging/TabNest.iss
```

产物在 `publish/installer/TabNest-0.1.0-setup.exe`。

发布产物是**单个自包含 exe**（11.1 MB）：不需要 .NET 运行时，不需要 VC++ 运行库。
安装包本身只是为了提供开始菜单项、开机自启选项与干净的卸载流程——
把 `TabNest.exe` 直接复制到任何位置双击也能用。

## 两个刻意的决定

**不要求管理员权限。** TabNest 只操作当前用户会话里的窗口。装成管理员反而有害：
高完整性级别的进程无法操作低完整性窗口，反过来也一样，提权安装会让一部分普通应用无法分组。

**卸载前先让运行中的实例正常退出。** TabNest 退出时会把所有被接管的窗口还原到原始位置
并恢复任务栏按钮。直接删文件而不走退出流程，用户的窗口会永久停在被接管的位置上，
任务栏按钮也回不来——卸载一个窗口管理工具却把窗口弄乱，是最不可接受的收尾。

配置与已保存分组（`%LOCALAPPDATA%\TabNest\`）在卸载时**保留**，它们是用户资产，重装后应当还在。
只有日志会被清理。

## 代码签名（尚未落实，需采购证书）

当前发布未签名，会有两个后果：

1. **SmartScreen 拦截**。用户首次运行时看到"Windows 已保护你的电脑"，
   需要点「更多信息 → 仍要运行」。新证书即使签了也要累积声誉才能免除这一步。
2. **杀软误报风险**。阶段一尚可，但**阶段二引入 DLL 注入后，未签名基本等于被判死刑**——
   全局注入是杀软最敏感的行为之一。

签名需要 OV 或 EV 代码签名证书（OV 约 ¥1500-3000/年；EV 更贵但能立即绕过 SmartScreen）。
证书采购需要由项目所有者完成，无法在开发阶段代办。

拿到证书后，在 `[Setup]` 段加：

```
SignTool=signtool sign /f "证书路径.pfx" /p "密码" /tr http://timestamp.digicert.com /td sha256 /fd sha256 $f
```

并对 `publish/v1/TabNest.exe` 单独签名后再打包——安装包和被安装的程序都要签。
