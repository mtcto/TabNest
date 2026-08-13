# 打包与发布

## 构建安装包

需要 [Inno Setup 6](https://jrsoftware.org/isdl.php)。

```bash
dotnet publish src/TabNest.App -c Release -r win-x64 -o publish/v1 --self-contained true -p:PublishTrimmed=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

```bash
iscc packaging/TabNest.iss
```

产物在 `publish/installer/TabNest-0.1.1-setup.exe`。

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

## 代码签名（官方发布未签名，需自行购证）

当前打包流程**默认不签名**。用户侧的 SmartScreen / 杀软提示，以及处理方式，写在仓库根目录 README 的「未签名版本」一节。

官方不代购、不代签。需要签名版的人自己买 **OV** 代码签名证书（DigiCert、Sectigo 等）。2024 年起 EV 不再能让 SmartScreen 首次放行，仅为过拦截不必买 EV。

拿到证书后：

1. 先签发布产物：

```
signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f "证书.pfx" /p "密码" publish\v1\TabNest.exe
```

2. 再在 `TabNest.iss` 的 `[Setup]` 段加：

```
SignTool=signtool sign /f "证书.pfx" /p "密码" /tr http://timestamp.digicert.com /td sha256 /fd sha256 $f
```

安装包和被安装的 `TabNest.exe` 都要签。开源项目也可以向 [SignPath Foundation](https://signpath.org/) 申请由其证书代签，那种方式签出来的发布者名称是 SignPath Foundation，不是你自己。
