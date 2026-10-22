using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OmenSuperHub.Services.NetworkBoost {
  /// <summary>WinINet 系统代理开关（HKCU Internet Settings + InternetSetOption 刷新）。
  /// Enable 前把用户原有 ProxyEnable/ProxyServer 快照进本 app 自己的注册表键，
  /// Disable 有快照则还原并删快照，无快照才写 0——否则用户原代理配置永久丢失。</summary>
  internal static class SystemProxyManager {
    const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    // 快照存自己的键（路径同 ConfigService.RegistryPath，勿跨文件引用）：崩溃后跨进程存活。
    const string SnapshotPath = @"Software\OmenXHub\ProxySnapshot";
    const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    const int INTERNET_OPTION_REFRESH = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static void Enable(string httpAddr, string socksAddr) {
      using (var key = Registry.CurrentUser.OpenSubKey(RegPath, true)) {
        if (key == null) return;
        WriteSnapshot(n => key.GetValue(n), SnapshotPath);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", "http=" + httpAddr + ";https=" + httpAddr + ";socks=" + socksAddr, RegistryValueKind.String);
      }
      Refresh();
    }

    public static void Disable() {
      using (var key = Registry.CurrentUser.OpenSubKey(RegPath, true)) {
        if (key == null) return;
        if (!RestoreSnapshot(key, SnapshotPath)) key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
      }
      Refresh();
    }

    // ponytail: 天花板——若崩溃且本程序再不启动，快照残留；用户下次手动开系统代理再关时
    // 走不到这里，无影响，但下次 Enable 会把他"加速期间"的配置当原值存。升级路径：启动时
    // 检测 ProxyEnable=1 且快照在 → 直接还原。现靠下次 Boost Start 的 StopInternal→Disable 兜底。
    static void WriteSnapshot(Func<string, object> get, string snapPath) {
      if (HasSnapshot(Registry.CurrentUser.OpenSubKey(snapPath))) return; // 已有快照不重复捕获
      var t = Capture(get);
      using (var snap = Registry.CurrentUser.CreateSubKey(snapPath)) {
        snap.SetValue("ProxyEnable", t.Item1, RegistryValueKind.DWord);
        snap.SetValue("ProxyServer", t.Item2, RegistryValueKind.String);
      }
    }

    static bool RestoreSnapshot(RegistryKey internet, string snapPath) {
      using (var snap = Registry.CurrentUser.OpenSubKey(snapPath)) {
        if (snap == null) return false;
        internet.SetValue("ProxyEnable", Convert.ToInt32(snap.GetValue("ProxyEnable", 0)), RegistryValueKind.DWord);
        internet.SetValue("ProxyServer", snap.GetValue("ProxyServer", "") as string ?? "", RegistryValueKind.String);
      }
      Registry.CurrentUser.DeleteSubKeyTree(snapPath, false);
      return true;
    }

    static Tuple<int, string> Capture(Func<string, object> get) {
      return Tuple.Create(Convert.ToInt32(get("ProxyEnable") ?? 0),
                          get("ProxyServer") as string ?? "");
    }

    static bool HasSnapshot(RegistryKey snap) {
      bool exists = snap != null;
      snap?.Dispose();
      return exists;
    }

    static void Refresh() {
      InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
      InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    /// <summary>--selftest: 在专用测试键上验证快照机制——Capture 抓原值、已有快照时重复
    /// WriteSnapshot 不覆盖、RestoreSnapshot 原样写回并删快照。真实快照存在（加速运行中）
    /// 则 SKIP，不触碰真值。</summary>
    public static string SelfCheck() {
      const string testRoot = @"Software\OmenXHub\ProxySnapshotSelfTest";
      const string testSnap = testRoot + @"\Snap";
      try {
        using (RegistryKey old = Registry.CurrentUser.OpenSubKey(SnapshotPath)) {
          if (old != null) return "SKIP proxy snapshot check (real snapshot present)";
        }
        using (var internet = Registry.CurrentUser.CreateSubKey(testRoot)) {
          internet.SetValue("ProxyEnable", 7, RegistryValueKind.DWord);
          internet.SetValue("ProxyServer", "http=1.2.3.4:8080", RegistryValueKind.String);
          var captured = Capture(n => internet.GetValue(n));
          if (captured.Item1 != 7 || captured.Item2 != "http=1.2.3.4:8080")
            return "FAIL proxy snapshot capture";
          WriteSnapshot(n => internet.GetValue(n), testSnap); // 模拟首次 Enable 存快照
          if (!HasSnapshot(Registry.CurrentUser.OpenSubKey(testSnap)))
            return "FAIL proxy snapshot not written";
          WriteSnapshot(n => n == "ProxyEnable" ? (object)9 : (object)"poison", testSnap); // 重复 Enable
          internet.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
          internet.SetValue("ProxyServer", "http=127.0.0.1:10800", RegistryValueKind.String);
          if (!RestoreSnapshot(internet, testSnap)) return "FAIL proxy snapshot restore returned false";
          if (Convert.ToInt32(internet.GetValue("ProxyEnable", -1)) != 7 ||
              (string)internet.GetValue("ProxyServer", "") != "http=1.2.3.4:8080")
            return "FAIL proxy snapshot restore wrong values"; // 含"重复捕获覆盖原值"回归
          if (HasSnapshot(Registry.CurrentUser.OpenSubKey(testSnap)))
            return "FAIL proxy snapshot not deleted after restore";
        }
      } catch (Exception ex) {
        return "FAIL proxy snapshot threw: " + ex.Message;
      } finally {
        Registry.CurrentUser.DeleteSubKeyTree(testRoot, false);
      }
      return "PASS proxy snapshot save/restore";
    }
  }
}
