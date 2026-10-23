using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace OmenSuperHub.Services {
  sealed class PresentMonFpsMonitor : IDisposable {
    const double SampleWindowSeconds = 1.5;
    static readonly TimeSpan CleanupInterval = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan StartFailureRetryInterval = TimeSpan.FromSeconds(10);
    /// <summary>同 PID 的最小解析间隔 —— FPS 是 1.5s 窗口平均值,200ms 采样足够。</summary>
    static readonly TimeSpan MinParseIntervalPerPid = TimeSpan.FromMilliseconds(200);
    /// <summary>1% low 的累计窗口。1.5s 在 60fps 下只有 ~90 帧,"最慢 1%" 会退化成 1 帧,
    /// 每拍数值乱跳 —— 10s 才够 ~600-1400 帧,1% ≈ 6-14 帧,才是个能读的指标。
    /// static readonly 而非 const:自检里要能对它做运行期断言,const 会让那行变成不可达代码(CS0162)。</summary>
    static readonly double LowWindowSeconds = 10;
    /// <summary>低帧分位:1% low = 最慢 1% 帧的平均帧时间取倒数(与 Afterburner/CapFrameX 同口径)。</summary>
    const double LowPercentile = 0.01;
    readonly List<double> _lowScratch = new List<double>();
    readonly Dictionary<int, DateTime> _lastParseUtc = new Dictionary<int, DateTime>();

    struct FpsSample {
      public DateTime TimestampUtc;
      public double MsBetweenPresents;
    }

    sealed class FpsWindow {
      public string Application;
      public string NormalizedApplication;
      public DateTime LastSeenUtc;
      public Queue<FpsSample> Samples = new Queue<FpsSample>();
      public double SampleTotalMs;
      /// <summary>与 Samples 同一批帧, 但保留 10s —— 1% low 不能用 1.5s 窗口(见 LowWindowSeconds)。</summary>
      public Queue<FpsSample> LowSamples = new Queue<FpsSample>();
    }

    readonly object syncRoot = new object();
    readonly Dictionary<int, FpsWindow> windowsByProcessId = new Dictionary<int, FpsWindow>();
    readonly Dictionary<string, FpsWindow> windowsByApplication = new Dictionary<string, FpsWindow>(StringComparer.OrdinalIgnoreCase);

    Process presentMonProcess;
    Thread stdoutThread;
    Thread stderrThread;
    bool disposed;
    bool headerParsed;
    int processIdColumnIndex = -1;
    int applicationColumnIndex = -1;
    int msBetweenPresentsColumnIndex = -1;
    string executablePath = string.Empty;
    string activeProcessNameFilter = string.Empty;
    int trackedProcessId = 0;
    string trackedProcessName = string.Empty;
    string statusText = "PresentMon 未启动";
    DateTime nextStartAttemptUtc = DateTime.MinValue;
    DateTime nextCleanupUtc = DateTime.MinValue;
    int _lastFps;
    int _lastFpsLow;
    string _lastApp;

    public int LastFps { get { lock (syncRoot) { return _lastFps; } } }
    /// <summary>1% low(见 LowPercentile)。0 = 暂无数据。</summary>
    public int LastFpsLow { get { lock (syncRoot) { return _lastFpsLow; } } }
    public string LastApp { get { lock (syncRoot) { return _lastApp; } } }

    public bool IsRunning {
      get {
        lock (syncRoot) {
          return presentMonProcess != null && !presentMonProcess.HasExited;
        }
      }
    }

    public bool EnsureRunning(string processNameFilter, out string message) {
      string cachedExecutablePath = string.Empty;
      string normalizedProcessNameFilter = NormalizeProcessName(processNameFilter);
      bool restartRequired = false;
      lock (syncRoot) {
        if (disposed) { message = "已释放"; return false; }
        if (presentMonProcess != null && !presentMonProcess.HasExited) {
          if (string.Equals(activeProcessNameFilter, normalizedProcessNameFilter, StringComparison.OrdinalIgnoreCase)) {
            message = statusText; return true;
          }
          restartRequired = true;
        }
        if (!restartRequired && DateTime.UtcNow < nextStartAttemptUtc) { message = statusText; return false; }
        cachedExecutablePath = executablePath;
      }

      if (restartRequired) Stop();

      string resolvedPath = ResolveExecutablePath(cachedExecutablePath);
      if (string.IsNullOrWhiteSpace(resolvedPath)) {
        lock (syncRoot) {
          executablePath = string.Empty;
          statusText = "未找到 PresentMon.exe";
          nextStartAttemptUtc = DateTime.UtcNow + StartFailureRetryInterval;
        }
        message = StatusText; return false;
      }

      var process = new Process();
      process.StartInfo = new ProcessStartInfo {
        FileName = resolvedPath,
        Arguments = BuildArguments(normalizedProcessNameFilter),
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(resolvedPath)
      };
      process.EnableRaisingEvents = true;
      process.Exited += (s, e) => { lock (syncRoot) { if (!disposed) statusText = "PresentMon 已停止"; } };

      try {
        if (!process.Start()) {
          lock (syncRoot) { executablePath = resolvedPath; statusText = "启动失败"; nextStartAttemptUtc = DateTime.UtcNow + StartFailureRetryInterval; }
          message = StatusText; process.Dispose(); return false;
        }
      } catch (Exception ex) {
        lock (syncRoot) { executablePath = resolvedPath; statusText = "启动失败: " + ex.Message; nextStartAttemptUtc = DateTime.UtcNow + StartFailureRetryInterval; }
        message = StatusText; process.Dispose(); return false;
      }

      lock (syncRoot) {
        executablePath = resolvedPath;
        presentMonProcess = process;
        headerParsed = false;
        processIdColumnIndex = -1;
        applicationColumnIndex = -1;
        msBetweenPresentsColumnIndex = -1;
        windowsByProcessId.Clear();
        windowsByApplication.Clear();
        _lastParseUtc.Clear();
        activeProcessNameFilter = normalizedProcessNameFilter;
        trackedProcessId = 0;
        trackedProcessName = normalizedProcessNameFilter;
        nextStartAttemptUtc = DateTime.MinValue;
        nextCleanupUtc = DateTime.MinValue;
        statusText = "采集中";
      }

      stdoutThread = new Thread(ReadStdoutLoop) { IsBackground = true, Name = "PMStdout" };
      stderrThread = new Thread(ReadStderrLoop) { IsBackground = true, Name = "PMStderr" };
      stdoutThread.Start(process);
      stderrThread.Start(process);
      message = StatusText;
      return true;
    }

    public void Stop() {
      Process processToStop = null;
      lock (syncRoot) {
        processToStop = presentMonProcess;
        presentMonProcess = null;
        windowsByProcessId.Clear();
        windowsByApplication.Clear();
        _lastParseUtc.Clear();
        headerParsed = false;
        activeProcessNameFilter = string.Empty;
        trackedProcessId = 0;
        trackedProcessName = string.Empty;
        nextStartAttemptUtc = DateTime.MinValue;
        nextCleanupUtc = DateTime.MinValue;
        if (!disposed) statusText = "已停止";
      }
      if (processToStop == null) return;
      try {
        if (!processToStop.HasExited) { processToStop.Kill(); processToStop.WaitForExit(1000); }
      } catch { } finally { processToStop.Dispose(); }
    }

    /// <summary>Poll current FPS — must be called from timer (UI-safe).</summary>
    public void Poll() {
      lock (syncRoot) {
        MaybeCleanupStaleSamples(DateTime.UtcNow);
        // Foreground process mode: track active window
        if (string.IsNullOrEmpty(activeProcessNameFilter)) {
          int pid = GetForegroundPid();
          if (pid > 0 && pid != trackedProcessId) {
            trackedProcessId = pid;
            trackedProcessName = "";
          }
          if (trackedProcessId > 0 && windowsByProcessId.TryGetValue(trackedProcessId, out var w) && TryComputeFps(w, out float fps)) {
            _lastFps = (int)Math.Round(fps);
            _lastApp = w.Application ?? "";
            _lastFpsLow = ComputeWindowLow(w);
            return;
          }
        } else {
          // Process name mode
          if (!string.IsNullOrWhiteSpace(trackedProcessName) && windowsByApplication.TryGetValue(trackedProcessName, out var w) && (DateTime.UtcNow - w.LastSeenUtc).TotalSeconds <= SampleWindowSeconds && TryComputeFps(w, out float fps)) {
            _lastFps = (int)Math.Round(fps);
            _lastApp = w.Application ?? "";
            _lastFpsLow = ComputeWindowLow(w);
            return;
          }
        }
        _lastFps = 0;
        _lastFpsLow = 0;
      }
    }

    static int GetForegroundPid() {
      try {
        var h = NativeMethods.GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        NativeMethods.GetWindowThreadProcessId(h, out uint pid);
        return (int)pid;
      } catch { return 0; }
    }

    static class NativeMethods {
      [System.Runtime.InteropServices.DllImport("user32.dll")]
      public static extern IntPtr GetForegroundWindow();
      [System.Runtime.InteropServices.DllImport("user32.dll")]
      public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }

    public void Dispose() { if (!disposed) { disposed = true; Stop(); } }

    static string NormalizeProcessName(string name) {
      if (string.IsNullOrWhiteSpace(name)) return string.Empty;
      string n = name.Trim();
      if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
      return n;
    }

    static string BuildArguments(string filter) {
      string args = "--output_stdout --exclude_dropped --no_console_stats --stop_existing_session --restart_as_admin";
      if (!string.IsNullOrWhiteSpace(filter)) {
        args += " --process_name " + QuoteArgument(filter);
        args += " --terminate_on_proc_exit";
      }
      return args;
    }

    static string QuoteArgument(string v) => string.IsNullOrEmpty(v) ? "\"\"" : "\"" + v.Replace("\"", "\\\"") + "\"";

    static string ResolveExecutablePath(string cached) {
      if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached)) return cached;
      string baseDir = AppDomain.CurrentDomain.BaseDirectory;
      string[] paths = {
        Path.Combine(baseDir, "PresentMon.exe"),
        Path.Combine(baseDir, "PresentMon64.exe"),
        Path.Combine(baseDir, "tools", "PresentMon.exe"),
        Path.Combine(baseDir, "tools", "PresentMon64.exe")
      };
      foreach (var p in paths) if (File.Exists(p)) return p;
      // Search PATH
      string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
      foreach (string dir in pathEnv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
        try {
          var found = Directory.GetFiles(dir.Trim(), "PresentMon*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
          if (found != null) return found;
        } catch { }
      }
      return "";
    }

    string StatusText { get { lock (syncRoot) { return statusText; } } }

    void ReadStdoutLoop(object state) {
      var p = state as Process;
      if (p == null) return;
      try { string l; while ((l = p.StandardOutput.ReadLine()) != null) { if (!string.IsNullOrWhiteSpace(l)) HandleCsvLine(l); } }
      catch { lock (syncRoot) { if (!disposed) statusText = "读取异常"; } }
    }

    void ReadStderrLoop(object state) {
      var p = state as Process;
      if (p == null) return;
      try { string l; while ((l = p.StandardError.ReadLine()) != null) { if (!string.IsNullOrWhiteSpace(l)) { lock (syncRoot) { if (!disposed) statusText = l.Trim(); } } } } catch { }
    }

    void HandleCsvLine(string line) {
      lock (syncRoot) {
        if (!headerParsed) {
          var cols = ParseCsvLine(line);
          if (cols.Count == 0) return;
          processIdColumnIndex = FindIndex(cols, "ProcessID");
          applicationColumnIndex = FindIndex(cols, "Application");
          msBetweenPresentsColumnIndex = FindIndex(cols, "msBetweenPresents");
          if (processIdColumnIndex >= 0 && applicationColumnIndex >= 0 && msBetweenPresentsColumnIndex >= 0) headerParsed = true;
          return;
        }
        // ponytail: PERF(fps-parse-throttle) —— PresentMon --output_stdout 每帧一行 CSV。
        // 帧率按 1.5s 窗口统计(msBetweenPresents 求平均),200ms 内同 PID 只取一行
        // 不影响 FPS 数值,只降采样密度。节流必须在 TryExtract **之前**:实测
        // TryExtract 占整条路径 71%(0.83/1.17 us),放在其后只能省 29%。
        // 廉价 PID 预扫描:行首是 Application(通常不带引号),PID 是第二列。
        // 带引号的行(应用名含逗号)返回 false → 不节流,直接走完整解析,行为不变。
        // 天花板: 每个 PID 一个字典项,进程退出后靠 CleanupStaleSamples 之外的
        // _lastParseUtc 只增不减 —— 数量级=同时出现的 PID 数(几十),可忽略。
        var now = DateTime.UtcNow;
        if (TryPeekPid(line, out int peekPid)) {
          if (_lastParseUtc.TryGetValue(peekPid, out var lastParse)
              && (now - lastParse) < MinParseIntervalPerPid) return;
          _lastParseUtc[peekPid] = now;
        }
        if (!TryExtract(line, out int pid, out string app, out double ms)) return;
        if (pid <= 0 || ms <= 0) return;
        string norm = NormalizeProcessName(app);
        if (!string.IsNullOrWhiteSpace(activeProcessNameFilter) && !string.Equals(activeProcessNameFilter, norm, StringComparison.OrdinalIgnoreCase)) return;
        if (!windowsByProcessId.TryGetValue(pid, out var w)) { w = new FpsWindow(); windowsByProcessId[pid] = w; }
        w.Application = app;
        w.NormalizedApplication = norm;
        w.LastSeenUtc = now;
        w.Samples.Enqueue(new FpsSample { TimestampUtc = now, MsBetweenPresents = ms });
        w.SampleTotalMs += ms;
        w.LowSamples.Enqueue(new FpsSample { TimestampUtc = now, MsBetweenPresents = ms });
        if (!string.IsNullOrWhiteSpace(norm)) windowsByApplication[norm] = w;
        CleanupWindow(w, now);
      }
    }

    /// <summary>从行首廉价取 PID(第二列)。行首带引号(应用名含逗号)时返回 false,
    /// 由调用方回退到完整解析 —— 宁可不节流,不能取错 PID。</summary>
    static bool TryPeekPid(string line, out int pid) {
      pid = 0;
      if (string.IsNullOrEmpty(line) || line[0] == '"') return false;
      int c1 = line.IndexOf(',');
      if (c1 <= 0) return false;
      int c2 = line.IndexOf(',', c1 + 1);
      if (c2 <= c1 + 1) return false;
      return int.TryParse(line.Substring(c1 + 1, c2 - c1 - 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && pid > 0;
    }

    bool TryExtract(string line, out int pid, out string app, out double ms) {
      pid = 0; app = ""; ms = 0;
      string pidStr = null, msStr = null;
      bool sawApp = false;
      int limit = Math.Max(processIdColumnIndex, Math.Max(applicationColumnIndex, msBetweenPresentsColumnIndex));
      bool inQ = false;
      int start = 0, col = 0;
      for (int i = 0; i <= line.Length; i++) {
        bool end = i == line.Length;
        char c = end ? '\0' : line[i];
        if (!end && c == '"') { inQ = !inQ; continue; }
        if (!end && (c != ',' || inQ)) continue;
        string val = col == processIdColumnIndex || col == applicationColumnIndex || col == msBetweenPresentsColumnIndex ? Unquote(line.Substring(start, i - start)) : null;
        if (col == processIdColumnIndex) pidStr = val;
        else if (col == applicationColumnIndex) { app = (val ?? "").Trim(); sawApp = true; }
        else if (col == msBetweenPresentsColumnIndex) msStr = val;
        if (col >= limit && pidStr != null && msStr != null && sawApp) break;
        col++; start = i + 1;
      }
      return pidStr != null && msStr != null && sawApp && int.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && double.TryParse(msStr, NumberStyles.Float, CultureInfo.InvariantCulture, out ms);
    }

    static int FindIndex(List<string> cols, string name) {
      for (int i = 0; i < cols.Count; i++) if (string.Equals(cols[i], name, StringComparison.OrdinalIgnoreCase)) return i;
      return -1;
    }

    void MaybeCleanupStaleSamples(DateTime now) {
      if (now < nextCleanupUtc) return;
      CleanupStaleSamples(now);
      nextCleanupUtc = now + CleanupInterval;
    }

    void CleanupStaleSamples(DateTime now) {
      List<int> remove = null;
      foreach (var kv in windowsByProcessId) {
        CleanupWindow(kv.Value, now);
        if (kv.Value.Samples.Count == 0 && (now - kv.Value.LastSeenUtc).TotalSeconds > SampleWindowSeconds) { if (remove == null) remove = new List<int>(); remove.Add(kv.Key); }
      }
      if (remove == null) return;
      foreach (int id in remove) {
        if (!windowsByProcessId.TryGetValue(id, out var w)) continue;
        windowsByProcessId.Remove(id);
        if (!string.IsNullOrWhiteSpace(w.NormalizedApplication) && windowsByApplication.TryGetValue(w.NormalizedApplication, out var cw) && ReferenceEquals(cw, w))
          windowsByApplication.Remove(w.NormalizedApplication);
      }
    }

    static void CleanupWindow(FpsWindow w, DateTime now) {
      while (w.Samples.Count > 0 && (now - w.Samples.Peek().TimestampUtc).TotalSeconds > SampleWindowSeconds) {
        var s = w.Samples.Dequeue(); w.SampleTotalMs -= s.MsBetweenPresents;
      }
      if (w.SampleTotalMs < 0) w.SampleTotalMs = 0;
      // 1% low 的样本活得更久, 单独按 10s 淘汰
      while (w.LowSamples.Count > 0 && (now - w.LowSamples.Peek().TimestampUtc).TotalSeconds > LowWindowSeconds)
        w.LowSamples.Dequeue();
    }

    /// <summary>1% low:把帧时间升序排序后取最慢 pct 比例那一段的平均,再取倒数。
    /// 就地排序传入的列表 —— 调用方复用同一个缓冲,免得每秒分配一个 List。</summary>
    internal static int ComputeLowFps(List<double> frameMs, double pct) {
      if (frameMs == null || frameMs.Count == 0) return 0;
      if (pct <= 0) pct = LowPercentile;
      frameMs.Sort();
      int n = (int)Math.Ceiling(frameMs.Count * pct);
      if (n < 1) n = 1;
      if (n > frameMs.Count) n = frameMs.Count;
      double sum = 0;
      for (int i = frameMs.Count - n; i < frameMs.Count; i++) sum += frameMs[i];
      double avg = sum / n;
      if (avg <= 0) return 0;
      return (int)Math.Round(1000.0 / avg);
    }

    int ComputeWindowLow(FpsWindow w) {
      _lowScratch.Clear();
      foreach (var s in w.LowSamples) _lowScratch.Add(s.MsBetweenPresents);
      return ComputeLowFps(_lowScratch, LowPercentile);
    }

    static bool TryComputeFps(FpsWindow w, out float fps) {
      fps = 0;
      if (w == null || w.Samples.Count == 0) return false;
      double avg = w.SampleTotalMs / w.Samples.Count;
      if (avg <= 0) return false;
      fps = (float)(1000.0 / avg);
      return true;
    }

    static List<string> ParseCsvLine(string line) {
      var cols = new List<string>();
      if (line == null) return cols;
      bool inQ = false;
      int start = 0;
      for (int i = 0; i < line.Length; i++) {
        if (line[i] == '"') inQ = !inQ;
        else if (line[i] == ',' && !inQ) { cols.Add(Unquote(line.Substring(start, i - start))); start = i + 1; }
      }
      cols.Add(Unquote(line.Substring(start)));
      return cols;
    }

    static string Unquote(string v) {
      if (string.IsNullOrEmpty(v)) return "";
      v = v.Trim();
      if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2).Replace("\"\"", "\"");
      return v;
    }

    /// <summary>--selftest: PID 预扫描 + 节流决策 —— 纯函数,不启 PresentMon。</summary>
    /// <remarks>
    /// ponytail: 节流把"每帧一行"降到"每 PID 每 200ms 一行"。它错了两头都有害:
    /// 取错 PID 会让不同应用互相节流(FPS 掉),不节流则白花解析。所以钉死:
    ///   正常行 → 取到第二列 PID;带引号行(应用名含逗号) → false(回退完整解析)。
    /// </remarks>
    internal static string SelfCheck() {
      var fail = new List<string>();
      int pid;

      if (!TryPeekPid("game.exe,1234,0x0,DXGI,1,0,0,Hardware: Legacy Flip,1.0", out pid) || pid != 1234)
        fail.Add("TryPeekPid 正常行应得 1234, got " + pid);
      if (!TryPeekPid("dwm.exe,42,x", out pid) || pid != 42)
        fail.Add("TryPeekPid 短行应得 42, got " + pid);
      // 行首带引号 = 应用名含逗号,列偏移不可信 → 必须放弃预扫描。
      // 这个样本在漏掉引号检查时会被误判成 PID=999(真实 PID 是 1234)。
      if (TryPeekPid("\"x,999,y\",1234,rest", out pid))
        fail.Add("TryPeekPid 带引号行必须返回 false(否则按错列取 PID=999), got " + pid);
      if (TryPeekPid("noCommasAtAll", out pid))
        fail.Add("TryPeekPid 无逗号行必须返回 false");
      if (TryPeekPid("app.exe,notanumber,x", out pid))
        fail.Add("TryPeekPid 非数字 PID 必须返回 false");
      if (TryPeekPid("app.exe,0,x", out pid))
        fail.Add("TryPeekPid PID=0 必须返回 false");
      if (TryPeekPid("", out pid))
        fail.Add("TryPeekPid 空串必须返回 false");
      if (TryPeekPid(null, out pid))
        fail.Add("TryPeekPid null 必须返回 false");

      // 节流间隔必须短于统计窗口,否则窗口内样本太少会让 FPS 抖动
      if (MinParseIntervalPerPid.TotalMilliseconds >= SampleWindowSeconds * 1000 / 3)
        fail.Add("节流间隔过长: " + MinParseIntervalPerPid.TotalMilliseconds + "ms vs 窗口 " + (SampleWindowSeconds * 1000) + "ms");

      // ── 1% low ──
      // 低帧窗口必须长于实时窗口,否则 1% 分位在 ~90 帧上退化成 1 帧,数值全在抖。
      if (LowWindowSeconds <= SampleWindowSeconds)
        fail.Add("1% low 窗口必须长于实时窗口: " + LowWindowSeconds + " vs " + SampleWindowSeconds);

      if (ComputeLowFps(new List<double>(), LowPercentile) != 0 || ComputeLowFps(null, LowPercentile) != 0)
        fail.Add("1% low 空样本应为 0");

      var uniform = new List<double>();
      for (int i = 0; i < 100; i++) uniform.Add(10);
      int u = ComputeLowFps(uniform, LowPercentile);
      if (u != 100) fail.Add("1% low 均匀 10ms 应为 100, got " + u);

      // 100 帧里 1 帧卡顿 → ceil(100*0.01)=1 帧进分位 → 1000/100 = 10
      var jitter = new List<double>();
      for (int i = 0; i < 99; i++) jitter.Add(16.7);
      jitter.Add(100);
      int j = ComputeLowFps(jitter, LowPercentile);
      if (j != 10) fail.Add("1% low 99×16.7ms+1×100ms 应为 10, got " + j);
      if (j > 1000.0 / (jitter.Sum() / jitter.Count))
        fail.Add("1% low 不应高于平均帧率");

      // 1000 帧里 10 帧慢帧 → 分位应取满 10 帧(验证 ceil 缩放,不是恒取 1 帧)
      var scaled = new List<double>();
      for (int i = 0; i < 990; i++) scaled.Add(10);
      for (int i = 0; i < 10; i++) scaled.Add(50);
      int sc = ComputeLowFps(scaled, LowPercentile);
      if (sc != 20) fail.Add("1% low 1000 帧取 10 慢帧应为 20, got " + sc);

      // 非法帧时间不得产生除零/负数
      if (ComputeLowFps(new List<double> { 0, 0 }, LowPercentile) != 0)
        fail.Add("1% low 全零帧时间应为 0(不得除零)");

      return fail.Count == 0 ? "PASS PresentMonFpsMonitor throttle + 1% low"
        : "FAIL:\n  " + string.Join("\n  ", fail);
    }
  }
}
