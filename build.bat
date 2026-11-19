@echo off
chcp 65001 >nul
rem ============================================================
rem  v2.4 一键编译脚本（正式版：不生成任何日志文件）
rem  使用 .NET Framework 自带 csc，无需 VS
rem  源码为 UTF-8 无 BOM，故指定 /codepage:65001
rem  正式版不定义 TRACE_LOG：Log 的所有调用在编译期被移除，运行时不写 log.txt
rem  需要日志排查问题时请改用 build-test.bat（输出到 _test\）
rem  v2.3：程序名改为 MultiWallCycle，新增 Lang.cs（双语文案表）
rem  v2.3 第三批：加 /win32icon（资源管理器里显示图标）+ /resource（运行期读同一份 ico）
rem  v2.4：新增 源码\ImageMeta.cs（JPEG 标题读写）
rem ============================================================
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set PC=C:\Windows\Microsoft.NET\assembly\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll
set WB=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll
set XAML=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll

if not exist "成品" mkdir "成品"

echo [1/1] 编译主程序（正式版）...
"%CSC%" /nologo /codepage:65001 /target:winexe /main:MultiWallCycle.Program ^
  /win32manifest:源码\app.manifest ^
  /win32icon:源码\app.ico ^
  /resource:源码\app.ico,app.ico ^
  /out:成品\MultiWallCycle.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  /r:"%WB%" /r:"%PC%" /r:"%XAML%" ^
  源码\AutoStart.cs 源码\AssemblyInfo.cs 源码\Bing.cs 源码\CaptionSettingsForm.cs ^
  源码\Config.cs 源码\IconFactory.cs 源码\Lang.cs 源码\Log.cs 源码\MainForm.cs ^
  源码\NativeMethods.cs 源码\Program.cs 源码\Uninstall.cs 源码\WallpaperEngine.cs ^
  源码\ImageMeta.cs
if errorlevel 1 (
  echo [失败] 主程序编译出错
  pause
  exit /b 1
)

echo.
echo [完成] 成品\MultiWallCycle.exe
echo.
echo 提示：若生成的 exe 立刻消失，多半被安全软件（如火绒）当作可疑程序拦截，
echo       请把本目录加入安全软件白名单后重新编译。
pause