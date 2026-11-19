# MultiWallCycle（多屏幻灯壁纸）

一款 Windows 桌面壁纸定时轮换工具，补足微软商店 Dynamic Theme 的短板：**每个显示器可设置不同壁纸与不同更换间隔**，基于 Windows 原生 `IDesktopWallpaper` COM 接口实现。支持中英文界面自动切换、开机自启与绿色便携配置。

- 当前版本：**v2.4.0.0**（正式版，绿色免安装）
- 运行环境：Windows 10 / 11（.NET Framework 4.x）
- 技术栈：C# / WinForms / WPF（PresentationCore、WindowsBase、System.Xaml）

## 目录结构

```
├─ 源码/            # C# 源码与图标
├─ 成品/            # MultiWallCycle.exe、使用说明、变更说明、sha256
├─ build.bat        # 一键编译脚本（正式版，不写日志）
├─ LICENSE          # GPL-3.0
└─ README.md
```

## 主要模块

| 源码文件 | 职责 |
|---|---|
| `Program.cs` | 程序入口 |
| `MainForm.cs` | 主界面（草稿制设置） |
| `WallpaperEngine.cs` | 壁纸轮换引擎（IDesktopWallpaper） |
| `Bing.cs` | Bing 壁纸获取 |
| `Config.cs` | 配置读写（支持便携 config.json） |
| `CaptionSettingsForm.cs` | 字幕/设置窗口 |
| `ImageMeta.cs` | 图片元信息（Exif） |
| `Lang.cs` | 中英文案表 |
| `AutoStart.cs` / `Uninstall.cs` | 开机自启 / 卸载清理 |
| `NativeMethods.cs` | Win32 互操作 |

## 从源码编译

双击 `build.bat`，或手动调用 .NET Framework 自带 `csc.exe`：

```powershell
.\build.bat
```

编译产物为 `成品\MultiWallCycle.exe`。需要日志排查时可参考 v2.5 测试版的 `build-test.bat`（输出到 `_test\`）。

> 若生成的 exe 立刻消失，多为安全软件（如火绒）拦截，请将目录加入白名单后重新编译；详见 `成品\杀软误报说明.md`。

## 使用

详见 `成品\使用说明.md`。把 `MultiWallCycle.exe` 放到固定文件夹后双击运行，每块屏分别选择壁纸文件夹与更换间隔，点「保存并应用」。

## 许可

本项目以 **GPL-3.0** 协议开源，详见 [LICENSE](LICENSE)。仅限非商业用途的学习、测试；作者不对使用过程中出现的问题负责，也不承诺后续版本升级。
