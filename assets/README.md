# 图标资源

`logo.jpg` 是图标源图。`src/TabNest.App/TabNest.ico` 由它生成，包含
16/20/24/32/40/48/64/128/256 九档尺寸。

重新生成：

```bash
dotnet run --file tools/make-icon.cs
```

## 为什么要备这么多档

Windows 按场景挑尺寸：托盘 16/20/24，任务栏 32/40，Alt+Tab 与资源管理器大图标
48/64，属性对话框与商店 256。缺哪一档系统就得现场缩放，缩出来的图在小尺寸下会糊成一团。

## 一个踩过的坑

`ApplicationIcon` 生成的**图标组**资源 ID 是 **32512**（IDI_APPLICATION），
组里各档单独的图标才编号 1..N。用 1 去 `LoadImage` 正好命中"单个图标"而非"图标组"，
返回 0，于是托盘与标题栏全是空白且没有任何报错。见 `AppIcon.MainIconResourceId`。