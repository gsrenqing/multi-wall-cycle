using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MultiWallCycle
{
    /// <summary>开机自启：HKCU\...\Run 注册表项，当前用户级，无需管理员权限。</summary>
    public static class AutoStartHelper
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>v2.3：自启值名随程序名改为英文 MultiWallCycle（注册表内也避免中文名）。</summary>
        private const string ValueName = "MultiWallCycle";

        /// <summary>旧版（v1.1.x~v2.2）的自启值名，v2.3 写入新值时一并清除，避免开机启动两个实例。</summary>
        private const string LegacyValueName = "多屏幻灯壁纸";

        public static void Set(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) { return; }
                    if (enable)
                    {
                        // v1.1.2 第三轮：自启命令带 -min，开机后窗口默认退入托盘（仅图标常驻）；
                        // 手动双击 exe（无参数）则正常显示主窗口。
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" -min");
                        // v2.3：启用时顺带迁走旧版值，避免开机同时启动新旧两个实例
                        if (key.GetValue(LegacyValueName) != null)
                        {
                            try { key.DeleteValue(LegacyValueName, false); } catch { }
                        }
                    }
                    else
                    {
                        // 关闭自启只删本程序自己的值名；旧版值名（可能是用户手工保留的指向旧 exe 的项）
                        // 不在这里动，留给「卸载」或用户自行处理，避免静默改动用户既有设置。
                        if (key.GetValue(ValueName) != null)
                        {
                            key.DeleteValue(ValueName, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("设置开机自启失败: " + ex.Message);
            }
        }

        /// <summary>是否已启用自启（v2.3 之后只看新值名；旧值名由 Set 迁移清理）。</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    return key != null && key.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
