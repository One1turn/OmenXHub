using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace OmenSuperHub.Services.NetworkBoost {
  /// <summary>启动/停止 sing-box TUN 进程，并清理 TUN 残留默认路由。</summary>
  internal static class TunManager {
    public static string BinDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");

    // ponytail: sing-box.exe 无 Authenticode 签名，不能走 ExtractAndPreloadNativeDll（会验签失败删文件）。
    // 单独提取到 bin\ 目录，wintun.dll 放同目录让 sing-box 加载。
    // ponytail: 嵌入资源为 .gz 压缩形式（sing-box 43MB→16MB），解压后写入 bin\。
    static readonly string[] _binaries = { "sing-box.exe", "wintun.dll" };

    public static void EnsureBinaries() {
      try {
        if (!Directory.Exists(BinDir)) Directory.CreateDirectory(BinDir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in _binaries) {
          string dest = Path.Combine(BinDir, name);
          // ponytail: 旧实现 `if (File.Exists(dest)) continue` 会把解压中途被杀留下的截断
          // exe 永久粘住(之后每次启动都跳过重提取)。改为 sidecar 记录完整落盘的字节数,
          // 尺寸不符/无 sidecar 即重新提取;提取写 temp 后原子换入,dest 路径上只可能出现
          // 完整文件。上限: 只防截断不防篡改(资源哈希比对见 InstallPawnIO,43MB 每次全
          // 提取代价不成比例)。
          if (File.Exists(dest) && SizeMatches(dest)) continue;
          var rn = Array.Find(asm.GetManifestResourceNames(),
            r => r.EndsWith(name + ".gz", StringComparison.OrdinalIgnoreCase));
          bool isGz = rn != null;
          if (!isGz) {
            rn = Array.Find(asm.GetManifestResourceNames(),
              r => r.EndsWith(name, StringComparison.OrdinalIgnoreCase));
          }
          if (rn == null) continue;
          string tmp = dest + ".tmp";
          try {
            using (var s = asm.GetManifestResourceStream(rn))
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)) {
              if (isGz) {
                using (var gz = new System.IO.Compression.GZipStream(s, System.IO.Compression.CompressionMode.Decompress))
                  gz.CopyTo(fs);
              } else {
                s.CopyTo(fs);
              }
            }
            long written = new FileInfo(tmp).Length;
            if (File.Exists(dest)) File.Replace(tmp, dest, null);
            else File.Move(tmp, dest);
            // sidecar 最后写: 换入成功但 sidecar 失败时,下次启动重提取一次即可(幂等,不粘滞)
            File.WriteAllText(dest + ".size", written.ToString());
          } catch {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
          }
        }
      } catch (Exception ex) { Logger.Warn("[TunManager] EnsureBinaries: " + ex.Message); }
    }

    static bool SizeMatches(string dest) {
      try {
        return long.TryParse(File.ReadAllText(dest + ".size"), out long sz)
               && sz > 0 && new FileInfo(dest).Length == sz;
      } catch { return false; }
    }

    public static string ConfigPath {
      get {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmenXHub");
        return Path.Combine(dir, "singbox-config.json");
      }
    }

    static readonly object _lock = new object();
    static Process _proc;
    static IntPtr _hJob;   // sing-box 的 kill-on-close Job(宿主死亡时由内核连带清掉孤儿)
    // 审查修复 #24: 旧代码 static _proc 无锁,且 Start 到 Process.Start 才赋值 —— OnExit(App.xaml.cs:372)
    // 的 Stop 先跑完、parked Start 后落 _proc,退出链不认识这个进程,而成孤儿 sing-box + 断网黑洞(见下方注释);
    // KILL_ON_JOB_CLOSE 兑底可缺(:134/:136 仅 Warn)。改:Stop 锁内置位并取走 _proc;Start spawn 后锁内
    // TryAdopt —— 置位则拒收,调用方就地 Kill,不再单靠可缺的 Job。
    static bool _shuttingDown;

    // 锁内认领 spawn 出的进程:退出标志已置 → 返 false,调用方负责 Kill/Dispose 局部句柄。
    static bool TryAdopt(Process p) {
      lock (_lock) {
        if (_shuttingDown) return false;
        _proc = p;
        return true;
      }
    }

    // ponytail: sing-box 绝不能活得比宿主久——宿主被杀/崩溃后 sing-box 残留时 TUN 默认路由仍在,
    // 出站全指向已死的 loopback 代理 → 全系统断网黑洞(App.OnExit 只覆盖优雅退出)。
    // KILL_ON_JOB_CLOSE 是唯一内核级兜底;Win8+ 允许嵌套 Job(计划任务启动场景可用)。
    static IntPtr CreateKillOnCloseJob() {
      IntPtr hJob = CpuAffinity.Kernel32.CreateJobObject(IntPtr.Zero, null);
      if (hJob == IntPtr.Zero) return IntPtr.Zero;
      var lim = new CpuAffinity.JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
        BasicLimitInformation = new CpuAffinity.JOBOBJECT_BASIC_LIMIT_INFORMATION {
          LimitFlags = CpuAffinity.JobLimitFlags.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        }
      };
      int size = System.Runtime.InteropServices.Marshal.SizeOf<CpuAffinity.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
      IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
      bool ok;
      try {
        System.Runtime.InteropServices.Marshal.StructureToPtr(lim, ptr, false);
        ok = CpuAffinity.Kernel32.SetInformationJobObject(hJob,
          CpuAffinity.JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, ptr, (uint)size);
      } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
      if (!ok) { CpuAffinity.Kernel32.CloseHandle(hJob); return IntPtr.Zero; }
      return hJob;
    }

    static void CloseJob() {
      if (_hJob != IntPtr.Zero) { CpuAffinity.Kernel32.CloseHandle(_hJob); _hJob = IntPtr.Zero; }
    }

    public static bool IsRunning {
      // 审查修复 #24: _proc 读写统一进同一把锁;Stop 的 Kill/WaitForExit(最长 ~7.5s)全在锁外,
      // 本属性不会因退出链等待被饿死。
      get { lock (_lock) { try { return _proc != null && !_proc.HasExited; } catch { return false; } } }
    }

    public static bool Start(string configPath, out string error) {
      error = "";
      // 审查修复 #24: 新起 Start 在锁内清位 —— BoostService.Start 内部先走 StopInternal
      // (BoostService.cs:67)→TunManager.Stop(BoostService.cs:150)再紧接 StartTun→本方法,
      // 不清位则每次合法重启都被 TryAdopt 误拒。清位之后的滞留窗口(EnsureBinaries 首次
      // 解压可达秒级、Process.Start)若 OnExit 的 Stop 插队置位,spawn 后会被拒收就地杀掉。
      lock (_lock) _shuttingDown = false;
      EnsureBinaries();
      string exe = Path.Combine(BinDir, "sing-box.exe");
      if (!File.Exists(exe)) { error = Strings.BoostSingboxMissing; return false; }
      try {
        var psi = new ProcessStartInfo {
          FileName = exe,
          Arguments = "run -c \"" + configPath + "\"",
          WorkingDirectory = BinDir,
          UseShellExecute = false,
          CreateNoWindow = true,
          WindowStyle = ProcessWindowStyle.Hidden
        };
        var p = Process.Start(psi);
        if (!TryAdopt(p)) {
          // 孤儿窗口闭合:本进程是退出链(OnExit→Stop)跑完后才落地的,没人认领 → 就地杀,
          // 不依赖可缺的 KILL_ON_JOB_CLOSE。
          try { p.Kill(); p.WaitForExit(3000); } catch { }
          try { p.Dispose(); } catch { }
          error = "应用正在退出,已取消启动 sing-box";
          return false;
        }
        Thread.Sleep(1500); // STARTUP_STABLE_DELAY
        if (p.HasExited) {
          error = "sing-box exited: code " + p.ExitCode;
          try { p.Dispose(); } catch { }
          lock (_lock) { if (_proc == p) _proc = null; }
          return false;
        }
        // 防孤儿兜底:创建/挂接失败只记警告,不拒绝启动(退回修复前行为,好过因防护装不上而不给加速)。
        // 审查修复 #24: Sleep 窗口内 Stop 可能已取走 _proc(进程已被它杀掉),此时不再建 Job,免留悬空句柄。
        lock (_lock) {
          if (_proc == p) {
            _hJob = CreateKillOnCloseJob();
            if (_hJob == IntPtr.Zero) {
              Logger.Warn("[TunManager] 防孤儿 Job 创建失败,sing-box 可能存活于宿主进程之外");
            } else if (!CpuAffinity.Kernel32.AssignProcessToJobObject(_hJob, p.Handle)) {
              Logger.Warn("[TunManager] sing-box 挂接防孤儿 Job 失败");
              CloseJob();
            }
          }
        }
        return true;
      } catch (Exception ex) {
        error = ex.Message;
        return false;
      }
    }

    public static void Stop() {
      // 审查修复 #24: 锁内只置位 + 取走 _proc/_hJob(纯字段搬移,微秒级);
      // Kill+WaitForExit(3s)与 route.exe(3s)全在锁外执行 —— 整方法持锁最坏 ≈7.5s
      // 会饿死 IsRunning/并发 Start,不可接受。退出链等待总时延与修复前同量级。
      Process p; IntPtr hJob;
      lock (_lock) {
        _shuttingDown = true;
        p = _proc; _proc = null;
        hJob = _hJob; _hJob = IntPtr.Zero;
      }
      try {
        if (p != null && !p.HasExited) {
          p.Kill();
          p.WaitForExit(3000);
        }
      } catch { }
      try { p?.Dispose(); } catch { }
      if (hJob != IntPtr.Zero) CpuAffinity.Kernel32.CloseHandle(hJob); // 关句柄连带 KILL_ON_JOB_CLOSE 补杀漏网成员
      try {
        var psi = new ProcessStartInfo("route.exe", "delete 0.0.0.0 mask 0.0.0.0 " + SingboxConfigGenerator.TunGateway) {
          UseShellExecute = false, CreateNoWindow = true
        };
        using (var rp = Process.Start(psi)) { if (rp != null) rp.WaitForExit(3000); }
      } catch { }
    }

    // 审查修复 #24 配套断言(--selftest 链 App.xaml.cs:69 现成调用点,同一行拼接,FAIL 判定不变):
    public static string SelfCheck() {
      return JobKillOnCloseSelfCheck() + "; " + ShutdownFlagSelfCheck();
    }

    // #24 断言:用未启动的 Process 做纯字段级验证(TryAdopt 只写字段;Stop 对未启动句柄的
    // HasExited 抛进 catch → 跳过 Kill;Dispose 安全)。三条不变量:
    // ①清位后正常认领成功且写入 _proc;②Stop 锁内置位;③置位时认领必被拒(parked Start 就地杀)。
    static string ShutdownFlagSelfCheck() {
      string r;
      try {
        lock (_lock) { _proc = null; _shuttingDown = false; }
        var p = new Process();
        if (!TryAdopt(p)) r = "FAIL TunManager shutdown-flag: 非退出态认领被拒";
        else {
          bool adopted;
          lock (_lock) { adopted = ReferenceEquals(_proc, p); _proc = null; }
          if (!adopted) r = "FAIL TunManager shutdown-flag: 认领未写入 _proc";
          else {
            Stop(); // 顺带跑 route.exe 清链,与真实退出路径同量级(数十 ms)
            bool flagged;
            lock (_lock) { flagged = _shuttingDown; }
            if (!flagged) r = "FAIL TunManager shutdown-flag: Stop 未置退出标志";
            else if (TryAdopt(new Process())) {
              lock (_lock) _proc = null;
              r = "FAIL TunManager shutdown-flag: 退出标志置位时 parked Start 认领成功";
            } else r = "PASS TunManager shutdown-flag";
          }
        }
      } catch (Exception ex) { r = "FAIL TunManager shutdown-flag: " + ex.Message; }
      finally { lock (_lock) { _proc = null; _shuttingDown = false; } }
      return r;
    }

    static string JobKillOnCloseSelfCheck() {
      // 验证 KILL_ON_JOB_CLOSE 内核语义:起 cmd→挂同型 Job→关句柄→断言进程死。
      try {
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") {
          UseShellExecute = false, CreateNoWindow = true
        };
        using (var p = Process.Start(psi)) {
          if (p == null) return "FAIL TunManager job selfcheck: 无法启动子进程";
          IntPtr hJob = CreateKillOnCloseJob();
          if (hJob == IntPtr.Zero) { try { p.Kill(); } catch { } return "FAIL TunManager job selfcheck: 无法创建 job"; }
          if (!CpuAffinity.Kernel32.AssignProcessToJobObject(hJob, p.Handle)) {
            CpuAffinity.Kernel32.CloseHandle(hJob);
            try { p.Kill(); } catch { }
            return "FAIL TunManager job selfcheck: AssignProcessToJobObject 失败";
          }
          CpuAffinity.Kernel32.CloseHandle(hJob);   // 关句柄应即杀子进程
          bool dead = p.WaitForExit(5000);
          if (!dead) { try { p.Kill(); } catch { } return "FAIL TunManager job selfcheck: 子进程在 CloseHandle 后存活"; }
          return "PASS TunManager job kill-on-close";
        }
      } catch (Exception ex) { return "FAIL TunManager job selfcheck: " + ex.Message; }
    }
  }
}
