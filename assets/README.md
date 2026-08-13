# 图标资源

图标是几何色块，不是插画：橙色圆角方底 + 一枚加粗白 T（TabNest）。
`tools/make-icon.cs` 按每个目标尺寸直接画，不从大图缩小。

- `logo.png`：1024 精绘稿（任务栏 / Alt+Tab / 资源管理器预览）
- `logo-tray.png`：256 平涂稿（与托盘同构图）
- `src/TabNest.App/TabNest.ico`：16/20/24/32/40/48/64/128/256 九档

重新生成：

```bash
dotnet run --file tools/make-icon.cs
```

## 为什么要按尺寸画

Windows 按场景挑尺寸：托盘 16/20/24，任务栏 32/40，Alt+Tab 与资源管理器大图标
48/64，属性对话框与商店 256。缺哪一档系统就得现场缩放，缩出来的图在小尺寸下会糊成一团。

16px 只剩 256 个像素。插画、毛发、吉祥物在这个尺寸不可辨，所以托盘档用整像素色块。

## 一个踩过的坑

`ApplicationIcon` 生成的**图标组**资源 ID 是 **32512**（IDI_APPLICATION），
组里各档单独的图标才编号 1..N。用 1 去 `LoadImage` 正好命中"单个图标"而非"图标组"，
返回 0，于是托盘与标题栏全是空白且没有任何报错。见 `AppIcon.MainIconResourceId`。