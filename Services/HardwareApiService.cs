// HardwareApiService.cs - 本地 HTTP REST API 服务
// HttpListener 监听 localhost:5000，提供 30+ 端点读写硬件设置（风扇/CPU/GPU/灯效/系统）
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmenSuperHub.Services;
using OmenSuperHub.Pages;
using static OmenSuperHub.OmenHardware;
using LibreHardwareType = LibreHardwareMonitor.Hardware.HardwareType;
using LibreSensorType = LibreHardwareMonitor.Hardware.SensorType;

namespace OmenSuperHub.Services {
  internal static class HardwareApiService {
    private static HttpListener _listener;
    private static CancellationTokenSource _cts;
    private static Task _listenTask;
    private static bool _running;
    private static string _apiToken;

    public static bool IsRunning => _running;
    public static string ApiToken => _apiToken;

    const int Port = 5000;
    // LAN 绑通配符 "+"(应用 requireAdministrator,可免 urlacl 直接绑);关时仍绑 localhost。
    static string CurrentPrefix => ConfigService.LanAccessEnabled
      ? $"http://+:{Port}/"
      : $"http://localhost:{Port}/";

    public static void Start(string prefix = null) {
      if (_running) return;
      try {
        _apiToken = ResolveToken();
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix ?? CurrentPrefix);
        _listener.Start();
        _running = true;
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
        // ponytail: LAN 关时也显式清规则 —— 进程被强杀会留陈旧规则,下次启动在此自愈。
        if (ConfigService.LanAccessEnabled) AddFirewallRule();
        else RemoveFirewallRule();
        Logger.Info($"HardwareApiService started on {prefix ?? CurrentPrefix} (LAN={ConfigService.LanAccessEnabled})");
      } catch (Exception ex) {
        Logger.Error($"HardwareApiService start failed: {ex.Message}");
        _running = false;
      }
    }

    // ponytail: 持久化令牌 —— 空则生成并存注册表;非持久化模式每次 Start 都换。
    static string ResolveToken() {
      if (!ConfigService.HttpApiPersistentToken) return Guid.NewGuid().ToString("N");
      if (string.IsNullOrEmpty(ConfigService.HttpApiToken)) {
        ConfigService.HttpApiToken = Guid.NewGuid().ToString("N");
        ConfigService.Save("HttpApiToken");
      }
      return ConfigService.HttpApiToken;
    }

    /// <summary>换新令牌并立即重启监听生效(固定令牌开关切换 / 重置令牌按钮)。</summary>
    public static void ResetToken() {
      ConfigService.HttpApiToken = Guid.NewGuid().ToString("N");
      ConfigService.Save("HttpApiToken");
      if (_running) { Stop(); Start(); }
    }

    public static void Stop() {
      if (!_running) return;
      _running = false;
      try { _cts?.Cancel(); } catch { }
      try { _listener?.Stop(); } catch { }
      try { _listener?.Close(); } catch { }
      try { _listenTask?.Wait(2000); } catch { }
      try { RemoveFirewallRule(); } catch { }
      Logger.Info("HardwareApiService stopped");
    }

    // ═══════════════════════════════════════════════════════
    // Windows Firewall (netsh) — 全 profile + 本地子网,幂等(先删后加)
    // ponytail: profile=any 而非 private —— 家用 Wi-Fi 常被 Windows 判为"公用网络",
    // 限定 private 会让规则在该网络上完全不生效。边界靠 remoteip=localsubnet 守住:
    // 仅同一子网可达,且仍有令牌鉴权。
    // ═══════════════════════════════════════════════════════
    const string FirewallRuleName = "OmenXHub LAN WebUI";

    static void RunNetsh(string args) {
      try {
        var psi = new System.Diagnostics.ProcessStartInfo("netsh", args) {
          UseShellExecute = false, CreateNoWindow = true,
          RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var p = System.Diagnostics.Process.Start(psi)) p?.WaitForExit(5000);
      } catch (Exception ex) { Logger.Warn($"netsh failed: {ex.Message}"); }
    }

    static void AddFirewallRule() {
      RunNetsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
      RunNetsh($"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={Port} profile=any remoteip=localsubnet");
    }

    static void RemoveFirewallRule() =>
      RunNetsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");


    private static async Task ListenLoop(CancellationToken ct) {
      while (!ct.IsCancellationRequested && _running) {
        try {
          HttpListenerContext ctx = await _listener.GetContextAsync();
          ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
        } catch (HttpListenerException) {
          break;
        } catch (ObjectDisposedException) {
          break;
        } catch (Exception ex) {
          Logger.Error($"HardwareApiService listen error: {ex.Message}");
        }
      }
    }

    // ═══════════════════════════════════════════════════════
    // Request Dispatch
    // ═══════════════════════════════════════════════════════

    private static void HandleRequest(HttpListenerContext ctx) {
      HttpListenerRequest req = ctx.Request;
      HttpListenerResponse resp = ctx.Response;
      string responseText;
      int statusCode = 200;

      try {
        string path = req.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        string method = req.HttpMethod.ToUpperInvariant();

        // ── 内置 WebUI:公开(不含机密,页面壳),token 由浏览器从 ?token= 取 ──
        if (method == "GET" && (path == "" || path == "/ui")) {
          SendHtml(resp, WebUiHtml);
          return;
        }

        if (path != "/ping") {
          if (!ValidateRequest(req, path)) {
            statusCode = 403;
            responseText = MakeError("Forbidden: invalid token or origin");
            SendResponse(resp, statusCode, responseText);
            return;
          }
        }

        // ── LAN 来源分级:非回环请求只放行监控读 + 预设/风扇写 ──
        if (!req.IsLocal && !IsLanAllowed(method, path)) {
          statusCode = 403;
          responseText = MakeError("Forbidden: endpoint not available over LAN");
          SendResponse(resp, statusCode, responseText);
          Logger.Warn($"API: LAN blocked {method} {path} from {req.RemoteEndPoint}");
          return;
        }

        // ── GET endpoints ──
        if (method == "GET" && path == "/ping") {
          responseText = "OK";
        } else if (method == "GET" && path == "/api/temperature") {
          responseText = HandleGetTemperature();
        } else if (method == "GET" && path == "/api/fan/speed") {
          responseText = HandleGetFanSpeed();
        } else if (method == "GET" && path == "/api/fan/rpm") {
          responseText = HandleGetFanRpm();
        } else if (method == "GET" && path == "/api/mode") {
          responseText = HandleGetMode();
        } else if (method == "GET" && path == "/api/cpu/load") {
          responseText = HandleGetCpuLoad();
        } else if (method == "GET" && path == "/api/cpu/frequency") {
          responseText = HandleGetCpuFrequency();
        } else if (method == "GET" && path == "/api/gpu/load") {
          responseText = HandleGetGpuLoad();
        } else if (method == "GET" && path == "/api/gpu/frequency") {
          responseText = HandleGetGpuFrequency();
        } else if (method == "GET" && path == "/api/gpu/memory") {
          responseText = HandleGetGpuMemory();
        } else if (method == "GET" && path == "/api/battery") {
          responseText = HandleGetBattery();
        } else if (method == "GET" && path == "/api/memory") {
          responseText = HandleGetMemory();
        } else if (method == "GET" && path == "/api/hardware/all") {
          responseText = HandleGetHardwareAll();
        }
        // ── POST endpoints ──
        else if (method == "POST" && path == "/api/memory/clean") {
          var result = HandleCleanMemory();
          responseText = result.body;
          statusCode = result.code;
        }
        else if (method == "POST" && path == "/api/fan/speed") {
          var result = HandleSetFanSpeed(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/power/limit") {
          var result = HandleSetPowerLimit(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/mode") {
          var result = HandleSetMode(req);
          responseText = result.body;
          statusCode = result.code;
        }
        // ── CPU write endpoints ──
        else if (method == "POST" && path == "/api/cpu/pl1") {
          var result = HandleSetCpuPl1(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/cpu/pl2") {
          var result = HandleSetCpuPl2(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/cpu/tdp") {
          var result = HandleSetCpuTdp(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/cpu/iccmax") {
          var result = HandleSetCpuIccMax(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/cpu/loadline") {
          var result = HandleSetCpuLoadLine(req);
          responseText = result.body;
          statusCode = result.code;
        }
        // ── GPU write endpoints ──
        else if (method == "POST" && path == "/api/gpu/core/offset") {
          var result = HandleSetGpuCoreOffset(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/gpu/memory/offset") {
          var result = HandleSetGpuMemoryOffset(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/gpu/tgp") {
          var result = HandleSetGpuTgp(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/gpu/ppab") {
          var result = HandleSetGpuPpab(req);
          responseText = result.body;
          statusCode = result.code;
        }
        // ── GET read endpoints ──
        else if (method == "GET" && path == "/api/cpu/power/current") {
          responseText = HandleGetCpuPowerCurrent();
        } else if (method == "GET" && path == "/api/gpu/offset/current") {
          responseText = HandleGetGpuOffsetCurrent();
        }
        // ── Fan curve endpoints ──
        else if (method == "GET" && path == "/api/fan/curve/current") {
          responseText = HandleGetFanCurveCurrent();
        } else if (method == "POST" && path == "/api/fan/curve") {
          var result = HandleSetFanCurve(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/fan/max") {
          var result = HandleSetFanMax(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "GET" && path == "/api/fan/mode") {
          responseText = HandleGetFanMode();
        } else if (method == "POST" && path == "/api/fan/mode") {
          var result = HandleSetFanMode(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/fan/clean") {
          var result = HandleSetFanClean(req);
          responseText = result.body;
          statusCode = result.code;
        }
        // ── System endpoints ──
        else if (method == "GET" && path == "/api/system/info") {
          responseText = HandleGetSystemInfo();
        } else if (method == "GET" && path == "/api/system/uptime") {
          responseText = HandleGetSystemUptime();
        } else if (method == "POST" && path == "/api/system/restart") {
          var result = HandleSystemRestart(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/system/shutdown") {
          var result = HandleSystemShutdown(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/system/display/off") {
          var result = HandleSystemDisplayOff();
          responseText = result.body;
          statusCode = result.code;
        }
        // ── Lighting endpoints ──
        else if (method == "GET" && path == "/api/lighting/status") {
          responseText = HandleGetLightingStatus();
        } else if (method == "POST" && path == "/api/lighting/brightness") {
          var result = HandleSetLightingBrightness(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/lighting/color") {
          var result = HandleSetLightingColor(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/lighting/effect") {
          var result = HandleSetLightingEffect(req);
          responseText = result.body;
          statusCode = result.code;
        } else if (method == "POST" && path == "/api/lighting/off") {
          var result = HandleSetLightingOff(req);
          responseText = result.body;
          statusCode = result.code;
        }
        // ── API status ──
        else if (method == "GET" && path == "/api/status") {
          responseText = HandleGetApiStatus();
        }
        // ── 404 ──
        else {
          statusCode = 404;
          responseText = "{\"error\":\"Not Found\"}";
        }
      } catch (Exception ex) {
        statusCode = 500;
        responseText = MakeError(ex.Message);
        Logger.Error($"HardwareApiService handle error: {ex.Message}");
      }

      SendResponse(resp, statusCode, responseText);
    }

    // ═══════════════════════════════════════════════════════
    // GET Handlers
    // ═══════════════════════════════════════════════════════

    private static string HandleGetTemperature() {
      float cpu = HardwareService.CPUTemp;
      bool gpuValid = HardwareService.TryGetFreshGpuTemp(out float gpu);
      return $"{{\"cpu\":{cpu:F1},\"gpu\":{gpu:F1},\"gpuValid\":{(gpuValid ? "true" : "false")}}}";
    }

    // 风扇档位在首次 EC 读取前为 -1 哨兵(HardwareService.cs:52),外发前钳 0。
    static int FanLevelAt(int i) {
      var f = HardwareService.FanSpeedNow;
      return f != null && f.Count > i && f[i] > 0 ? f[i] : 0;
    }

    private static string HandleGetFanSpeed() {
      return $"{{\"fan1\":{FanLevelAt(0)},\"fan2\":{FanLevelAt(1)}}}";
    }

    private static string HandleGetMode() {
      string mode = ConfigService.Preset;
      if (string.IsNullOrEmpty(mode)) mode = "Default";
      return $"{{\"mode\":\"{EscapeJson(mode)}\"}}";
    }

    // ── /api/fan/rpm ──
    private static string HandleGetFanRpm() {
      return $"{{\"fan1\":{FanLevelAt(0) * 100},\"fan2\":{FanLevelAt(1) * 100}}}";
    }

    // ── /api/cpu/load ──
    private static string HandleGetCpuLoad() {
      return $"{{\"avg\":{HardwareService.CPUUsage:F1},\"cores\":{{}}}}";
    }

    // ── /api/cpu/frequency ──
    private static string HandleGetCpuFrequency() {
      // ponytail: 必须 "{X:F0}" + "}" —— 写成 $"...{X:F0}}}" 时格式说明符后的 }} 会被
      // 并进格式串(编译期解析规则),输出字面量 "F2923" 而非数值,JSON 解析直接失败。
      return $"{{\"freq\":{HardwareService.CPUClock:F0}" + "}";
    }

    // ── /api/gpu/load ──
    private static string HandleGetGpuLoad() {
      return $"{{\"load\":{HardwareService.GPUUsage:F1}" + "}";
    }

    // ── /api/gpu/frequency ──
    private static string HandleGetGpuFrequency() {
      return $"{{\"freq\":{HardwareService.GPUClock:F0}" + "}";
    }

    // ── /api/gpu/memory ──
    private static string HandleGetGpuMemory() {
      float used, total;
      ReadGpuMemory(out used, out total);
      float available = total > used ? total - used : 0;
      return $"{{\"used\":{used:F0},\"total\":{total:F0},\"available\":{available:F0}" + "}";
    }

    /// <summary>读 GPU 显存占用/总量(MB)。/api/gpu/memory 与 /api/hardware/all 共用。</summary>
    private static void ReadGpuMemory(out float used, out float total) {
      used = 0; total = 0;
      // ponytail: 不调 hw.Update() —— QueryHardware 的 800ms 大循环已在同一批 GPU 对象
      // 上 Update 过,sensor .Value 读的是同一底层存储;HTTP 线程再 Update 会与轮询线程
      // 并发操作无锁的 LHM 实例(读数互踩)。去掉后与 /api/gpu/load|frequency|temperature
      // 三个姊妹端点同口径(≤800ms 陈旧但正确),新鲜度语义本就一致。
      foreach (var hw in HardwareService.LibreComputer.Hardware) {
        if (hw.HardwareType == LibreHardwareType.GpuNvidia || hw.HardwareType == LibreHardwareType.GpuAmd) {
          foreach (var s in hw.Sensors) {
            if (s.SensorType == LibreSensorType.SmallData && s.Name == "GPU Memory Used")
              used = (float)s.Value.GetValueOrDefault();
            if (s.SensorType == LibreSensorType.SmallData && s.Name == "GPU Memory Total")
              total = (float)s.Value.GetValueOrDefault();
          }
        }
      }
    }

    // ── /api/battery ──
    private static string HandleGetBattery() {
      try {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        int pct = (int)(ps.BatteryLifePercent * 100);
        bool charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        string status = charging ? "Charging" : "Discharging";
        if (pct >= 100) status = "Full";
        return $"{{\"percent\":{pct},\"charging\":{(charging ? "true" : "false")},\"status\":\"{status}\",\"wear\":0}}";
      } catch {
        return "{\"percent\":0,\"charging\":false,\"status\":\"Unknown\",\"wear\":0}";
      }
    }

    // ── /api/memory ── 运存/虚拟内存(与仪表盘同源: NativeMethods_Proc.GetMemoryStatus)
    private static string HandleGetMemory() {
      try {
        ReadMemory(out double usedMB, out double totalMB, out double pct,
                   out double virtUsedMB, out double virtTotalMB);
        return $"{{\"usedMB\":{usedMB:F0},\"totalMB\":{totalMB:F0},\"percent\":{pct:F1}," +
               $"\"virtualUsedMB\":{virtUsedMB:F0},\"virtualTotalMB\":{virtTotalMB:F0}" + "}";
      } catch (Exception ex) {
        Logger.Error($"HandleGetMemory: {ex.Message}");
        return "{\"usedMB\":0,\"totalMB\":0,\"percent\":0,\"virtualUsedMB\":0,\"virtualTotalMB\":0}";
      }
    }

    /// <summary>读系统运存/虚拟内存(MB 与百分比)。/api/memory 与 /api/hardware/all 共用。</summary>
    private static void ReadMemory(out double usedMB, out double totalMB, out double percent,
                                   out double virtUsedMB, out double virtTotalMB) {
      var mem = NativeMethods_Proc.GetMemoryStatus();
      ulong used = mem.ullTotalPhys - mem.ullAvailPhys;
      usedMB = used / 1048576.0;
      totalMB = mem.ullTotalPhys / 1048576.0;
      percent = mem.ullTotalPhys > 0 ? (double)used * 100 / mem.ullTotalPhys : 0;
      virtUsedMB = (mem.ullTotalPageFile - mem.ullAvailPageFile) / 1048576.0;
      virtTotalMB = mem.ullTotalPageFile / 1048576.0;
    }

    // ── /api/hardware/all ──
    private static string HandleGetHardwareAll() {
      try {
        float cpuTemp = HardwareService.CPUTemp;
        bool gpuValid = HardwareService.TryGetFreshGpuTemp(out float gpuTemp);
        float cpuPower = HardwareService.CPUPower;
        var fans = HardwareService.FanSpeedNow;
        int f1 = fans.Count > 0 ? fans[0] : 0;
        int f2 = fans.Count > 1 ? fans[1] : 0;

        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        int batPct = (int)(ps.BatteryLifePercent * 100);
        bool charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

        string preset = ConfigService.Preset;
        if (string.IsNullOrEmpty(preset)) preset = "Default";

        // 显存/运存: 与 /api/gpu/memory | /api/memory 同一口径(本文件共用 helper)
        ReadGpuMemory(out float vramUsed, out float vramTotal);
        ReadMemory(out double memUsedMB, out double memTotalMB, out double memPct,
                   out double virtUsedMB, out double virtTotalMB);

        return "{" +
          $"\"cpu\":{{\"temp\":{cpuTemp:F1},\"load\":{HardwareService.CPUUsage:F1}," +
          $"\"freq\":{HardwareService.CPUClock:F0},\"cores\":{{}}," +
          $"\"power\":{cpuPower:F1}" + "}," +
          $"\"gpu\":{{\"temp\":{gpuTemp:F1},\"valid\":{(gpuValid ? "true" : "false")}," +
          $"\"load\":{HardwareService.GPUUsage:F1},\"freq\":{HardwareService.GPUClock:F0}," +
          $"\"power\":{(HardwareService.GpuPowerUsable ? HardwareService.GPUPower : 0):F1}," +
          $"\"memoryUsed\":{vramUsed:F0},\"memoryTotal\":{vramTotal:F0}" + "}," +
          $"\"mem\":{{\"usedMB\":{memUsedMB:F0},\"totalMB\":{memTotalMB:F0},\"percent\":{memPct:F1}," +
          $"\"virtualUsedMB\":{virtUsedMB:F0},\"virtualTotalMB\":{virtTotalMB:F0}" + "}," +
          $"\"fan\":{{\"percent\":[{f1},{f2}],\"rpm\":[{f1 * 100},{f2 * 100}]}}," +
          $"\"battery\":{{\"percent\":{batPct},\"charging\":{(charging ? "true" : "false")}," +
          $"\"status\":\"{(charging ? "Charging" : "Discharging")}\",\"wear\":0}}," +
          $"\"preset\":\"{EscapeJson(preset)}\"" +
          "}";
      } catch (Exception ex) {
        Logger.Error($"HandleGetHardwareAll error: {ex.Message}");
        return MakeError(ex.Message);
      }
    }

    // ═══════════════════════════════════════════════════════
    // POST Handlers
    // ═══════════════════════════════════════════════════════

    // ── /api/memory/clean ── 复用 AutomationProcessor.CleanMemory(与仪表盘「一键清理」同一实现)
    private static (int code, string body) HandleCleanMemory() {
      try {
        long freed = AutomationProcessor.CleanMemory();
        double freedMB = freed / 1048576.0;
        Logger.Info($"API: CleanMemory freed {freedMB:F1} MB");
        return (200, $"{{\"success\":true,\"freedMB\":{freedMB:F1}" + "}");
      } catch (Exception ex) {
        Logger.Error($"HandleCleanMemory: {ex.Message}");
        return (500, MakeError(ex.Message));
      }
    }

    private static (int code, string body) HandleSetFanSpeed(HttpListenerRequest req) {
      try {
        string body = ReadBody(req);
        var param = Deserialize<FanSpeedParam>(body);
        if (param == null)
          return (400, MakeError("Invalid JSON body"));

        if (param.speed1 < 0 || param.speed1 > 100 || param.speed2 < 0 || param.speed2 > 100)
          return (400, MakeError("speed1 and speed2 must be between 0 and 100"));

        OmenHardware.SetFanLevel(param.speed1, param.speed2, fan3: OmenHardware.IsThreeFan());
        Logger.Info($"API: SetFanLevel({param.speed1}, {param.speed2})");
        return (200, "{\"success\":true,\"message\":\"Fan speed updated\"}");
      } catch (Exception ex) {
        Logger.Error($"HandleSetFanSpeed error: {ex.Message}");
        return (500, MakeError(ex.Message));
      }
    }

    private static (int code, string body) HandleSetPowerLimit(HttpListenerRequest req) {
      try {
        string body = ReadBody(req);
        var param = Deserialize<PowerLimitParam>(body);
        if (param == null)
          return (400, MakeError("Invalid JSON body"));

        // 审查修复: 三处功耗域对齐产品地板/天花板 [10,254](RaplPowerLimitService.WattMin 注释
        // "与 WMI 路径语义对齐"+RestoreCpuPower/ApplyPresetHardware 均 ≥10) —— 旧 5W 低于地板
        // (RAPL 通道静默钳到 10,行為不一致),/api/cpu/pl1|pl2|tdp 的 [15,120] 天花板连本机
        // 出厂默认 130W 都恢复不了。
        if (param.pl1 < 10 || param.pl1 > 254 || param.pl2 < 10 || param.pl2 > 254)
          return (400, MakeError("pl1 and pl2 must be between 10 and 254"));

        bool ok = OmenHardware.SetCpuPowerLimit((byte)param.pl1, (byte)param.pl2);
        Logger.Info($"API: SetCpuPowerLimit({param.pl1}, {param.pl2}) => {ok}");
        if (ok)
          return (200, "{\"success\":true,\"message\":\"CPU power limit updated\"}");
        else
          return (200, "{\"success\":false,\"message\":\"WMI call failed\"}");
      } catch (Exception ex) {
        Logger.Error($"HandleSetPowerLimit error: {ex.Message}");
        return (500, MakeError(ex.Message));
      }
    }

    private static readonly string[][] PresetAliases = new string[][] {
      new string[] { "Extreme", "extreme", "极致性能", "极限", "极致" },
      new string[] { "GpuPriority", "gpupriority", "gpu优先", "gpu", "GPU优先" },
      new string[] { "LightUse", "lightuse", "轻度使用", "light", "轻度" },
      new string[] { "Custom1", "custom1", "自定义1", "自定义预设1" },
      new string[] { "Custom2", "custom2", "自定义2", "自定义预设2" },
      new string[] { "Custom3", "custom3", "自定义3", "自定义预设3" },
    };

    private static string ResolvePresetKey(string input) {
      if (string.IsNullOrEmpty(input)) return null;
      string trimmed = input.Trim();
      foreach (var group in PresetAliases) {
        foreach (var alias in group) {
          if (string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase))
            return group[0];
        }
      }
      return null;
    }

    private static (int code, string body) HandleSetMode(HttpListenerRequest req) {
      try {
        string body = ReadBody(req);
        var param = Deserialize<ModeParam>(body);
        if (param == null || string.IsNullOrWhiteSpace(param.mode))
          return (400, MakeError("Missing 'mode' parameter"));

        string presetKey = ResolvePresetKey(param.mode);
        if (presetKey == null)
          return (400, MakeError("Unknown preset. Supported: Extreme, GpuPriority, LightUse, Custom1-3"));

        AutomationProcessor.ApplyPreset(presetKey);
        Logger.Info($"API: ApplyPreset(\"{presetKey}\")");

        return (200, $"{{\"success\":true,\"message\":\"Switched to {EscapeJson(presetKey)}\"}}");
      } catch (Exception ex) {
        Logger.Error($"HandleSetMode error: {ex.Message}");
        return (500, MakeError(ex.Message));
      }
    }

    // ═══════════════════════════════════════════════════════
    // CPU Write Handlers
    // ═══════════════════════════════════════════════════════

    private static (int code, string body) HandleSetCpuPl1(HttpListenerRequest req) {
      try {
        var param = Deserialize<IntValueParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        if (param.value < 10 || param.value > 254)
          return (400, MakeError("value must be between 10 and 254"));
        bool ok = OmenHardware.SetCpuPowerLimitPL1Only((byte)param.value);
        Logger.Info($"API: SetCpuPowerLimitPL1Only({param.value}) => {ok}");
        return (200, ok
          ? "{\"success\":true,\"message\":\"PL1 updated\"}"
          : "{\"success\":false,\"message\":\"WMI call failed\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetCpuPl1: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSetCpuPl2(HttpListenerRequest req) {
      try {
        var param = Deserialize<IntValueParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        if (param.value < 10 || param.value > 254)
          return (400, MakeError("value must be between 10 and 254"));
        bool ok = OmenHardware.SetCpuPowerLimitPL2Only((byte)param.value);
        Logger.Info($"API: SetCpuPowerLimitPL2Only({param.value}) => {ok}");
        return (200, ok
          ? "{\"success\":true,\"message\":\"PL2 updated\"}"
          : "{\"success\":false,\"message\":\"WMI call failed\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetCpuPl2: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSetCpuTdp(HttpListenerRequest req) {
      try {
        var param = Deserialize<IntValueParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        if (param.value < 10 || param.value > 254)
          return (400, MakeError("value must be between 10 and 254"));
        bool ok = OmenHardware.SetConcurrentTdp((byte)param.value);
        Logger.Info($"API: SetConcurrentTdp({param.value}) => {ok}");
        return (200, ok
          ? "{\"success\":true,\"message\":\"TDP updated\"}"
          : "{\"success\":false,\"message\":\"WMI call failed\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetCpuTdp: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSetCpuIccMax(HttpListenerRequest req) {
      try {
        var param = Deserialize<IntValueParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        // R15/BUG-R15-2: EC 有效域 {0} ∪ [160,255](1-159 死区致 throttling/hang,见 PerfPage
        // IccMaxNum_ValueChanged 注释)。旧门 50-250 会把 50-159 原样下发。
        if (param.value != 0 && (param.value < 160 || param.value > 255))
          return (400, MakeError("value must be 0 or between 160 and 255"));
        OmenHardware.SetIccMaxByWmi((decimal)param.value);
        Logger.Info($"API: SetIccMaxByWmi({param.value})");
        return (200, "{\"success\":true,\"message\":\"IccMax updated\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetCpuIccMax: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSetCpuLoadLine(HttpListenerRequest req) {
      try {
        var param = Deserialize<IntValueParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        // R15/BUG-R15-2: SetLoadLine 参数是档位索引(0=不设置,1..15 对应 mΩ 档,见 PerfPage
        // AcLoadLineCombo Tag)。旧门 80-130 把"mΩ 值"当档位校验 —— 拒绝一切合法档位、
        // 放行 80-130 非法档位直写 EC。
        if (param.value < 0 || param.value > 15)
          return (400, MakeError("value must be between 0 and 15 (level index)"));
        OmenHardware.SetLoadLine(param.value);
        Logger.Info($"API: SetLoadLine({param.value})");
        return (200, "{\"success\":true,\"message\":\"LoadLine updated\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetCpuLoadLine: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    // ═══════════════════════════════════════════════════════
    // GPU Write Handlers
    // ═══════════════════════════════════════════════════════

    private static (int code, string body) HandleSetGpuCoreOffset(HttpListenerRequest req) {
      try {
        var param = Deserialize<IntOffsetParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        if (param.offset < -200 || param.offset > 300)
          return (400, MakeError("offset must be between -200 and 300"));
        GpuAppManager.SetCoreClockOffset(param.offset);
        Logger.Info($"API: SetCoreClockOffset({param.offset})");
        return (200, "{\"success\":true,\"message\":\"GPU core offset updated\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetGpuCoreOffset: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSetGpuMemoryOffset(HttpListenerRequest req) {
      try {
        var param = Deserialize<IntOffsetParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        if (param.offset < -500 || param.offset > 1000)
          return (400, MakeError("offset must be between -500 and 1000"));
        GpuAppManager.SetMemoryClockOffset(param.offset);
        Logger.Info($"API: SetMemoryClockOffset({param.offset})");
        return (200, "{\"success\":true,\"message\":\"GPU memory offset updated\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetGpuMemoryOffset: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSetGpuTgp(HttpListenerRequest req) {
      try {
        var param = Deserialize<IntValueParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        if (param.value < 80 || param.value > 150)
          return (400, MakeError("value must be between 80 and 150"));
        // Enable TGP via WMI BIOS and set NVIDIA power limit if available
        OmenHardware.SetGpuPowerState(true, ConfigService.PpabEnabled, ConfigService.DState);
        GpuAppManager.SetPowerLimit(param.value);
        Logger.Info($"API: SetGpuTgp({param.value}W) => tgp enabled, nvidia limit set");
        return (200, "{\"success\":true,\"message\":\"GPU TGP updated\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetGpuTgp: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSetGpuPpab(HttpListenerRequest req) {
      try {
        var param = Deserialize<BoolParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        OmenHardware.SetGpuPowerState(ConfigService.TgpEnabled, param.enabled, ConfigService.DState);
        Logger.Info($"API: SetGpuPowerState(tgp={ConfigService.TgpEnabled}, ppab={param.enabled})");
        return (200, "{\"success\":true,\"message\":\"GPU PPAB updated\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetGpuPpab: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    // ═══════════════════════════════════════════════════════
    // GET Read Handlers (current values)
    // ═══════════════════════════════════════════════════════

    private static string HandleGetCpuPowerCurrent() {
      // PL1/PL2 cannot be read back from WMI BIOS; return configured values
      float pl1 = ConfigService.CpuPowerPl1 > 0 ? ConfigService.CpuPowerPl1 : 0;
      float pl2 = ConfigService.CpuPowerPl2 > 0 ? ConfigService.CpuPowerPl2 : 0;
      return $"{{\"pl1\":{pl1:F0},\"pl2\":{pl2:F0},\"tdp\":{HardwareService.CPUPower:F1},\"iccmax\":{ConfigService.IccMax},\"loadline\":{ConfigService.AcLoadLine}}}";
    }

    private static string HandleGetGpuOffsetCurrent() {
      try {
        int core = GpuAppManager.GetCoreClockOffset();
        int memory = GpuAppManager.GetMemoryClockOffset();
        return $"{{\"core\":{core},\"memory\":{memory}}}";
      } catch {
        return "{\"core\":0,\"memory\":0}";
      }
    }

    // ═══════════════════════════════════════════════════════
    // Fan Curve Handlers
    // ═══════════════════════════════════════════════════════

    private static string HandleGetFanCurveCurrent() {
      try {
        var curve = FanService.LoadCustomCurve();
        var sb = new StringBuilder("[");
        for (int i = 0; i < curve.Count; i++) {
          if (i > 0) sb.Append(",");
          sb.Append($"{{\"temp\":{curve[i].temp:F0},\"speed\":{curve[i].rpm}}}");
        }
        sb.Append("]");
        return $"{{\"cpu\":{sb},\"gpu\":{sb}}}";
      } catch (Exception ex) {
        Logger.Error($"HandleGetFanCurveCurrent: {ex.Message}");
        return "{\"cpu\":[],\"gpu\":[]}";
      }
    }

    private static (int code, string body) HandleSetFanCurve(HttpListenerRequest req) {
      try {
        var param = Deserialize<FanCurveParam>(ReadBody(req));
        if (param == null || param.points == null || param.points.Count < 2)
          return (400, MakeError("points array required, minimum 2 points"));

        var points = new List<(float temp, int rpm)>();
        foreach (var p in param.points) {
          if (p.temp < 0 || p.temp > 100 || p.speed < 0 || p.speed > 100)
            return (400, MakeError("temp must be 0-100, speed must be 0-100"));
          // Convert percentage (0-100) to display RPM (= EC level ×100) —— 与本服务 GET
          // /api/fan/speed 的 ×100 口径及 FanService.ParseFanRpm 对称,保证写 50% 读回 5000。
          // ponytail: EC 满速字节是 60(cool 曲线注释"6000=BIOS 上限")、63(API 旧×64 口径)
          // 还是 100("max"档 SetFanLevel(100))三套并存,需真机 EC 表定标后全项目统一;
          // 此处先收往返一致性(旧 ×64 与读侧 ×100 不对称,写读一轮值就变)。
          points.Add(((float)p.temp, p.speed * 100));
        }

        FanService.SaveCustomCurve(points);
        FanService.ApplyCustomCurve(points);
        FanService.InitSmartFanState(ConfigService.SmartFanEmaAlpha);
        Logger.Info($"API: SetFanCurve with {points.Count} points");
        return (200, "{\"success\":true,\"message\":\"Fan curve updated\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetFanCurve: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static string HandleGetFanMode() {
      // 映射同 FanPage.LoadConfigState: smart/custom→smart; RPM/% 字样→manual; 其余按 FanTable 三内置档。
      try {
        string fc = ConfigService.FanControl ?? "";
        if (fc == "smart" || fc == "custom") return "{\"mode\":\"smart\"}";
        if (fc != "" && fc != "auto" && fc != "silent" && fc != "cool" && fc != "balanced")
          return "{\"mode\":\"manual\"}";
        string t = ConfigService.FanTable;
        if (t != "cool" && t != "balanced") t = "silent";
        return "{\"mode\":\"" + t + "\"}";
      } catch { return "{\"mode\":\"silent\"}"; }
    }

    private static (int code, string body) HandleSetFanMode(HttpListenerRequest req) {
      try {
        var param = Deserialize<ModeParam>(ReadBody(req));
        string mode = param?.mode?.Trim().ToLowerInvariant();
        // 遥控=切 FanPage「风扇配置」下拉五档; manual 档只是保持已存 RPM(不接新转速值), 直写转速仍仅回环。
        if (mode != "silent" && mode != "cool" && mode != "balanced" && mode != "smart" && mode != "manual")
          return (400, MakeError("mode must be one of: silent, cool, balanced, smart, manual"));

        // 复刻 FanPage.FanMode_SelectionChanged 的服务层序列(OSD/下拉/UI 槽位跳过 ——
        // HTTP 线程不触 UI; fanControlTimer 是 System.Threading.Timer, Change 线程安全)。
        TrayService.ResetAutoProtect();
        if (mode == "smart") {
          ConfigService.FanControl = "smart";
          ConfigService.Save("FanControl");
          var sp = FanService.LoadPresetSmartParams(ConfigService.Preset);
          if (sp.HasValue) {
            ConfigService.SmartFanEmaAlpha = sp.Value.emaAlpha;
            ConfigService.SmartFanStepDownRate = sp.Value.stepDown;
            ConfigService.SmartFanHysteresis = sp.Value.hysteresis;
          }
          FanService.InitSmartFanState(ConfigService.SmartFanEmaAlpha);
          FanService.ApplyPresetCurve(ConfigService.Preset);
        } else if (mode == "manual") {
          // 同 UI manual 分支: 把最后保存的 RPM 原样重写, 远程不接新转速值。
          int rpm = FanService.ParseFanRpm(ConfigService.FanControl);
          ConfigService.FanControl = rpm + " RPM";
          ConfigService.Save("FanControl");
          OmenHardware.SetMaxFanSpeedOff();
          // ponytail: FIX(no-zero-dip + manual keep-alive) —— 去掉归零中间态,并保持心跳
          // (旧实现关表 + 0,0 中间态:风扇先停一下,且 EC 约 110s 自行归零)。
          OmenHardware.SetFanLevel(rpm / 100, rpm / 100, fan3: OmenHardware.IsThreeFan());
          TrayService.fanControlTimer?.Change(0, 1000);
          Logger.Info($"API: SetFanMode(manual, hold {rpm} RPM)");
          return (200, "{\"success\":true,\"message\":\"Fan mode: manual (" + rpm + " RPM)\"}");
        } else {
          ConfigService.FanControl = "";
          ConfigService.FanTable = mode;
          ConfigService.Save("FanControl");
          ConfigService.Save("FanTable");
          FanService.LoadFanConfig(mode + ".txt");
        }
        OmenHardware.SetMaxFanSpeedOff();
        TrayService.fanControlTimer?.Change(0, 1000);
        Logger.Info($"API: SetFanMode({mode})");
        return (200, "{\"success\":true,\"message\":\"Fan mode: " + mode + "\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetFanMode: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSetFanMax(HttpListenerRequest req) {
      try {
        var param = Deserialize<BoolParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        if (param.enabled)
          OmenHardware.SetMaxFanSpeedOn();
        else
          OmenHardware.SetMaxFanSpeedOff();
        Logger.Info($"API: SetMaxFanSpeed({param.enabled})");
        return (200, $"{{\"success\":true,\"message\":\"Max fan {(param.enabled ? "enabled" : "disabled")}\"}}");
      } catch (Exception ex) { Logger.Error($"HandleSetFanMax: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    // ── CTS for fan clean task (prevent leak on repeated calls) ──
    static System.Threading.CancellationTokenSource _fanCleanCts;
    static readonly object _fanCleanLock = new object();

    private static (int code, string body) HandleSetFanClean(HttpListenerRequest req) {
      try {
        System.Threading.CancellationToken token;
        // ponytail: lock 串行化 CTS 换页 —— 旧实现两个并发 HTTP 线程同过 null 检查后,
        // 后跑的对已 Dispose 实例 Cancel 抛 ObjectDisposedException(整段 500)。
        lock (_fanCleanLock) {
          try { _fanCleanCts?.Cancel(); } catch { }
          _fanCleanCts?.Dispose();
          _fanCleanCts = new System.Threading.CancellationTokenSource();
          token = _fanCleanCts.Token;
        }
        System.Threading.Tasks.Task.Run(async () => {
          // 照 FanPage 标准路径:暂停 1s 心跳(否则每秒正常转速写覆盖 +128 逆转字节,
          // 30s 清灰里两者打架),载荷 (0,0)+fanClean 与 UI 一致,三扇机第 3 扇参与。
          TrayService.fanControlTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
          try {
            OmenHardware.SetFanLevel(0, 0, OmenHardware.IsThreeFan(), true);
            await System.Threading.Tasks.Task.Delay(30000, token);
          } catch (OperationCanceledException) {
            // Expected during shutdown or new fan clean request
          } catch (Exception ex) {
            Logger.Error($"FanClean error: {ex.Message}");
          } finally {
            // 被新请求取代时不恢复 —— 新任务自己的窗口不能被子节点的 {0,0}+重启心跳中止。
            if (!token.IsCancellationRequested) {
              try { OmenHardware.SetFanLevel(0, 0); } catch { }
              TrayService.fanControlTimer?.Change(0, 1000);
              Logger.Info("API: Fan clean completed/restored");
            } else {
              Logger.Info("API: Fan clean superseded by new request");
            }
          }
        });
        Logger.Info("API: Fan clean started (30s)");
        return (200, "{\"success\":true,\"message\":\"Fan clean started for 30 seconds\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetFanClean: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    // ═══════════════════════════════════════════════════════
    // System Handlers
    // ═══════════════════════════════════════════════════════

    private static string HandleGetSystemInfo() {
      try {
        string bios = OmenHardware.GetBiosVersion();
        string cpu = OmenHardware.GetCpuModel();
        string os = Environment.OSVersion.ToString();
        string dotnet = Environment.Version.ToString();
        string machine = Environment.MachineName;
        return $"{{\"machine\":\"{EscapeJson(machine)}\",\"bios\":\"{EscapeJson(bios)}\"," +
               $"\"cpu\":\"{EscapeJson(cpu)}\",\"os\":\"{EscapeJson(os)}\",\"dotnet\":\"{EscapeJson(dotnet)}\"}}";
      } catch (Exception ex) { Logger.Error($"HandleGetSystemInfo: {ex.Message}"); return MakeError(ex.Message); }
    }

    private static string HandleGetSystemUptime() {
      try {
        // Environment.TickCount 在 net481 是 Int32,~24.9 天回绕为负 → 曾报负秒数。
        var uptime = TimeSpan.FromMilliseconds(GetTickCount64());
        return $"{{\"seconds\":{(long)uptime.TotalSeconds},\"formatted\":\"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m\"}}";
      } catch (Exception ex) { return MakeError(ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern ulong GetTickCount64();

    private static (int code, string body) HandleSystemRestart(HttpListenerRequest req) {
      try {
        var param = Deserialize<ConfirmParam>(ReadBody(req));
        if (param == null || !param.confirm)
          return (400, MakeError("confirm must be true"));
        Logger.Info("API: System restart requested");
        System.Diagnostics.Process.Start("shutdown", "/r /t 5 /c \"OmenXHub API restart\"")?.Dispose();
        return (200, "{\"success\":true,\"message\":\"System will restart in 5 seconds\"}");
      } catch (Exception ex) { Logger.Error($"HandleSystemRestart: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSystemShutdown(HttpListenerRequest req) {
      try {
        var param = Deserialize<ConfirmParam>(ReadBody(req));
        if (param == null || !param.confirm)
          return (400, MakeError("confirm must be true"));
        Logger.Info("API: System shutdown requested");
        System.Diagnostics.Process.Start("shutdown", "/s /t 5 /c \"OmenXHub API shutdown\"")?.Dispose();
        return (200, "{\"success\":true,\"message\":\"System will shutdown in 5 seconds\"}");
      } catch (Exception ex) { Logger.Error($"HandleSystemShutdown: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    // ── /api/system/display/off ── 关闭显示器。任何键鼠输入即唤醒, 无损可逆, 故不要求 confirm;
    // 与 PerfPage「关闭显示器」按钮共用 NativeMethods_Display.TurnOffDisplay。
    private static (int code, string body) HandleSystemDisplayOff() {
      try {
        NativeMethods_Display.TurnOffDisplay();
        Logger.Info("API: TurnOffDisplay");
        return (200, "{\"success\":true,\"message\":\"Display off\"}");
      } catch (Exception ex) {
        Logger.Error($"HandleSystemDisplayOff: {ex.Message}");
        return (500, MakeError(ex.Message));
      }
    }

    // ═══════════════════════════════════════════════════════
    // Lighting Handlers
    // ═══════════════════════════════════════════════════════

    private static string HandleGetLightingStatus() {
      try {
        var colors = OmenLighting.GetZoneStaticColor();
        byte brightness = OmenLighting.GetZoneBrightness();
        int effect = OmenLighting.GetCurrentAnimationEffect();
        string colorHex = colors != null && colors.Length > 0
          ? $"#{colors[0].R:X2}{colors[0].G:X2}{colors[0].B:X2}" : "#000000";
        return $"{{\"available\":true,\"brightness\":{brightness},\"color\":\"{colorHex}\",\"effect\":{effect}}}";
      } catch {
        return "{\"available\":false}";
      }
    }

    private static (int code, string body) HandleSetLightingBrightness(HttpListenerRequest req) {
      try {
        var param = Deserialize<BrightnessParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        if (param.value < 0 || param.value > 100)
          return (400, MakeError("value must be between 0 and 100"));
        OmenLighting.SetZoneBrightness(OmenLighting.LightingDevice.Keyboard, (byte)param.value);
        Logger.Info($"API: SetLightingBrightness({param.value})");
        return (200, "{\"success\":true,\"message\":\"Brightness updated\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetLightingBrightness: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSetLightingColor(HttpListenerRequest req) {
      try {
        var param = Deserialize<ColorParam>(ReadBody(req));
        if (param == null) return (400, MakeError("Invalid JSON body"));
        if (param.r < 0 || param.r > 255 || param.g < 0 || param.g > 255 || param.b < 0 || param.b > 255)
          return (400, MakeError("r/g/b must be between 0 and 255"));
        var color = System.Windows.Media.Color.FromRgb((byte)param.r, (byte)param.g, (byte)param.b);
        OmenLighting.SetZoneStaticColor(OmenLighting.LightingDevice.Keyboard,
            new List<System.Windows.Media.Color> { color }, 100,
            OmenLighting.LightingControlInterface.BasicFourZone);
        Logger.Info($"API: SetLightingColor({param.r},{param.g},{param.b})");
        return (200, "{\"success\":true,\"message\":\"Color updated\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetLightingColor: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSetLightingEffect(HttpListenerRequest req) {
      try {
        var param = Deserialize<EffectParam>(ReadBody(req));
        if (param == null || string.IsNullOrWhiteSpace(param.effect))
          return (400, MakeError("Missing 'effect' parameter"));
        Logger.Info($"API: SetLightingEffect(\"{param.effect}\")");
        // ponytail: 保 200 契约(端点已被 /api/help 自描述公开),但如实标 applied:false ——
        // 固件动画下发未接,旧实现直接返回 success:true+"applied" 是假成功。
        // 接 OmenLighting 动画映射超此端点范围,留待有消费者时再补。
        return (200, $"{{\"success\":true,\"applied\":false,\"message\":\"Effect '{EscapeJson(param.effect)}' not applied (not implemented)\"}}");
      } catch (Exception ex) { Logger.Error($"HandleSetLightingEffect: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    private static (int code, string body) HandleSetLightingOff(HttpListenerRequest req) {
      try {
        OmenLighting.SetZoneOff(OmenLighting.LightingDevice.Keyboard,
            OmenLighting.LightingControlInterface.BasicFourZone);
        Logger.Info("API: SetLightingOff");
        return (200, "{\"success\":true,\"message\":\"Lighting turned off\"}");
      } catch (Exception ex) { Logger.Error($"HandleSetLightingOff: {ex.Message}"); return (500, MakeError(ex.Message)); }
    }

    // ═══════════════════════════════════════════════════════
    // API Status
    // ═══════════════════════════════════════════════════════

    private static string HandleGetApiStatus() {
      return _apiStatusJson;
    }

    // ═══════════════════════════════════════════════════════
    // Security
    // ═══════════════════════════════════════════════════════

    // ponytail: 同源判定 —— LAN WebUI 的浏览器会带 http://<局域网IP>:5000 的 Origin,
    // 旧逻辑用 IsLoopback 会把它当跨站拒掉。放行"回环 OR 与请求同 host:port";
    // 跨站 / DNS 重绑定(Origin 指向别处)仍被拦,CSRF 防护不回退。
    static bool IsSameOriginOrLoopback(Uri reqUrl, string uriStr) {
      if (string.IsNullOrEmpty(uriStr)) return true;
      if (!Uri.TryCreate(uriStr, UriKind.Absolute, out var uri)) return false;
      if (uri.IsLoopback) return true;
      return reqUrl != null
        && uri.Port == reqUrl.Port
        && string.Equals(uri.Host, reqUrl.Host, StringComparison.OrdinalIgnoreCase);
    }

    // ── LAN 来源白名单:只放行监控读 + 性能预设 + 风扇模式切换(FanPage 五档下拉) + 内存清理;其余仅回环 ──
    // ponytail: 用户明确 LAN 遥控风扇=只用「风扇配置」模式下拉。直写转速(/api/fan/speed)、
    // 曲线数值(/api/fan/curve)、跑满开关(/api/fan/max)、吹灰(/api/fan/clean)全部仅回环。
    // 内存清理属无损维护动作(trim 工作集,不丢数据),且是用户要在 WebUI 里用的,故放行。
    // 关屏同理(任何输入即唤醒); 重启/关机仍严格仅回环。
    static readonly System.Collections.Generic.HashSet<string> LanReadPaths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) {
      "/ping", "/api/status", "/api/temperature",
      "/api/fan/speed", "/api/fan/rpm", "/api/fan/curve/current", "/api/fan/mode",
      "/api/mode", "/api/cpu/load", "/api/cpu/frequency", "/api/cpu/power/current",
      "/api/gpu/load", "/api/gpu/frequency", "/api/gpu/memory", "/api/gpu/offset/current",
      "/api/battery", "/api/memory", "/api/hardware/all", "/api/system/info", "/api/system/uptime",
      "/api/lighting/status",
    };
    static readonly System.Collections.Generic.HashSet<string> LanWritePaths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) {
      "/api/mode", "/api/fan/mode", "/api/memory/clean", "/api/system/display/off",
    };

    /// <summary>纯函数:该 (method, path) 是否允许非回环来源访问。</summary>
    internal static bool IsLanAllowed(string method, string path) {
      if (method == "GET") return LanReadPaths.Contains(path);
      if (method == "POST") return LanWritePaths.Contains(path);
      return false;
    }

    private static bool ValidateRequest(HttpListenerRequest req, string path) {
      string origin = req.Headers["Origin"];
      string referer = req.UrlReferrer?.ToString();
      if (!string.IsNullOrEmpty(origin) && !IsSameOriginOrLoopback(req.Url, origin)) {
        Logger.Error($"API: Blocked cross-origin request from {origin}");
        return false;
      }
      if (!string.IsNullOrEmpty(referer) && !IsSameOriginOrLoopback(req.Url, referer)) {
        Logger.Error($"API: Blocked cross-referer request from {referer}");
        return false;
      }

      string authHeader = req.Headers["Authorization"];
      if (!string.IsNullOrEmpty(authHeader)) {
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
          string token = authHeader.Substring(7).Trim();
          if (token == _apiToken) return true;
        }
      }

      string tokenParam = req.QueryString["token"];
      if (!string.IsNullOrEmpty(tokenParam) && tokenParam == _apiToken) return true;

      Logger.Error($"API: Unauthorized request to {path}");
      return false;
    }

    // ═══════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════

    [DataContract]
    private class FanSpeedParam {
      [DataMember] public int speed1 { get; set; }
      [DataMember] public int speed2 { get; set; }
    }

    [DataContract]
    private class PowerLimitParam {
      [DataMember] public int pl1 { get; set; }
      [DataMember] public int pl2 { get; set; }
    }

    [DataContract]
    private class ModeParam {
      [DataMember] public string mode { get; set; }
    }

    [DataContract]
    private class IntValueParam {
      [DataMember] public int value { get; set; }
    }

    [DataContract]
    private class IntOffsetParam {
      [DataMember] public int offset { get; set; }
    }

    [DataContract]
    private class BoolParam {
      [DataMember] public bool enabled { get; set; }
    }

    [DataContract]
    private class ConfirmParam {
      [DataMember] public bool confirm { get; set; }
    }

    [DataContract]
    private class FanCurveParam {
      [DataMember] public List<FanPointParam> points { get; set; }
    }

    [DataContract]
    private class FanPointParam {
      [DataMember] public int temp { get; set; }
      [DataMember] public int speed { get; set; }
    }

    [DataContract]
    private class BrightnessParam {
      [DataMember] public int value { get; set; }
    }

    [DataContract]
    private class ColorParam {
      [DataMember] public int r { get; set; }
      [DataMember] public int g { get; set; }
      [DataMember] public int b { get; set; }
    }

    [DataContract]
    private class EffectParam {
      [DataMember] public string effect { get; set; }
    }

    // ── Cached JSON serializers (avoid reflection per request) ──
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, DataContractJsonSerializer> _serializerCache
      = new System.Collections.Concurrent.ConcurrentDictionary<Type, DataContractJsonSerializer>();

    static DataContractJsonSerializer GetSerializer<T>() {
      return _serializerCache.GetOrAdd(typeof(T), t => new DataContractJsonSerializer(t));
    }

    // ── Cached API status response (40+ endpoints, only built once) ──
    static readonly string _apiStatusJson = BuildApiStatusJson();

    static string BuildApiStatusJson() {
      var endpoints = new string[] {
        "{\"method\":\"GET\",\"path\":\"/ping\",\"description\":\"Health check\"}",
        "{\"method\":\"GET\",\"path\":\"/api/status\",\"description\":\"API endpoint list\"}",
        "{\"method\":\"GET\",\"path\":\"/api/temperature\",\"description\":\"CPU/GPU temperature\"}",
        "{\"method\":\"GET\",\"path\":\"/api/fan/speed\",\"description\":\"Fan speed percent\"}",
        "{\"method\":\"GET\",\"path\":\"/api/fan/rpm\",\"description\":\"Fan speed RPM\"}",
        "{\"method\":\"GET\",\"path\":\"/api/fan/curve/current\",\"description\":\"Current fan curve\"}",
        "{\"method\":\"GET\",\"path\":\"/api/fan/mode\",\"description\":\"Current fan preset (silent/cool/balanced/smart/manual)\"}",
        "{\"method\":\"GET\",\"path\":\"/api/mode\",\"description\":\"Current preset mode\"}",
        "{\"method\":\"GET\",\"path\":\"/api/cpu/load\",\"description\":\"CPU load (per-core)\"}",
        "{\"method\":\"GET\",\"path\":\"/api/cpu/frequency\",\"description\":\"CPU frequency\"}",
        "{\"method\":\"GET\",\"path\":\"/api/cpu/power/current\",\"description\":\"CPU power settings\"}",
        "{\"method\":\"GET\",\"path\":\"/api/gpu/load\",\"description\":\"GPU load\"}",
        "{\"method\":\"GET\",\"path\":\"/api/gpu/frequency\",\"description\":\"GPU frequency\"}",
        "{\"method\":\"GET\",\"path\":\"/api/gpu/memory\",\"description\":\"GPU memory usage\"}",
        "{\"method\":\"GET\",\"path\":\"/api/gpu/offset/current\",\"description\":\"GPU clock offsets\"}",
        "{\"method\":\"GET\",\"path\":\"/api/battery\",\"description\":\"Battery status\"}",
        "{\"method\":\"GET\",\"path\":\"/api/memory\",\"description\":\"System memory usage\"}",
        "{\"method\":\"GET\",\"path\":\"/api/hardware/all\",\"description\":\"All hardware data\"}",
        "{\"method\":\"GET\",\"path\":\"/api/system/info\",\"description\":\"System information\"}",
        "{\"method\":\"GET\",\"path\":\"/api/system/uptime\",\"description\":\"System uptime\"}",
        "{\"method\":\"GET\",\"path\":\"/api/lighting/status\",\"description\":\"Keyboard lighting status\"}",
        "{\"method\":\"POST\",\"path\":\"/api/fan/speed\",\"description\":\"Set fan speed\"}",
        "{\"method\":\"POST\",\"path\":\"/api/fan/curve\",\"description\":\"Set fan curve\"}",
        "{\"method\":\"POST\",\"path\":\"/api/fan/max\",\"description\":\"Toggle max fan\"}",
        "{\"method\":\"POST\",\"path\":\"/api/fan/clean\",\"description\":\"Fan clean (30s)\"}",
        "{\"method\":\"POST\",\"path\":\"/api/mode\",\"description\":\"Switch preset\"}",
        "{\"method\":\"POST\",\"path\":\"/api/memory/clean\",\"description\":\"Clean system memory\"}",
        "{\"method\":\"POST\",\"path\":\"/api/power/limit\",\"description\":\"Set CPU PL1+PL2\"}",
        "{\"method\":\"POST\",\"path\":\"/api/cpu/pl1\",\"description\":\"Set CPU PL1\"}",
        "{\"method\":\"POST\",\"path\":\"/api/cpu/pl2\",\"description\":\"Set CPU PL2\"}",
        "{\"method\":\"POST\",\"path\":\"/api/cpu/tdp\",\"description\":\"Set CPU TDP\"}",
        "{\"method\":\"POST\",\"path\":\"/api/cpu/iccmax\",\"description\":\"Set CPU IccMax\"}",
        "{\"method\":\"POST\",\"path\":\"/api/cpu/loadline\",\"description\":\"Set CPU LoadLine\"}",
        "{\"method\":\"POST\",\"path\":\"/api/gpu/core/offset\",\"description\":\"Set GPU core offset\"}",
        "{\"method\":\"POST\",\"path\":\"/api/gpu/memory/offset\",\"description\":\"Set GPU memory offset\"}",
        "{\"method\":\"POST\",\"path\":\"/api/gpu/tgp\",\"description\":\"Toggle GPU TGP\"}",
        "{\"method\":\"POST\",\"path\":\"/api/gpu/ppab\",\"description\":\"Toggle GPU PPAB\"}",
        "{\"method\":\"POST\",\"path\":\"/api/lighting/brightness\",\"description\":\"Set keyboard brightness\"}",
        "{\"method\":\"POST\",\"path\":\"/api/lighting/color\",\"description\":\"Set keyboard color\"}",
        "{\"method\":\"POST\",\"path\":\"/api/lighting/effect\",\"description\":\"Set keyboard effect\"}",
        "{\"method\":\"POST\",\"path\":\"/api/lighting/off\",\"description\":\"Turn off keyboard lighting\"}",
        "{\"method\":\"POST\",\"path\":\"/api/system/restart\",\"description\":\"Restart system\"}",
        "{\"method\":\"POST\",\"path\":\"/api/system/display/off\",\"description\":\"Turn off display\"}",
        "{\"method\":\"POST\",\"path\":\"/api/system/shutdown\",\"description\":\"Shutdown system\"}"
      };
      return $"{{\"status\":\"running\",\"version\":\"1.0.0\",\"endpoints\":[{string.Join(",",endpoints)}]}}";
    }

    private static string ReadBody(HttpListenerRequest req) {
      // 仅 localhost+token,但仍钳 1MB 防异常大 POST 体进内存(Deserialize 也吃不下更大结构)。
      if (req.ContentLength64 > 1_000_000) return "";
      using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
        return reader.ReadToEnd();
    }

    private static T Deserialize<T>(string json) where T : class {
      if (string.IsNullOrWhiteSpace(json)) return null;
      var ser = GetSerializer<T>();
      using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        return ser.ReadObject(ms) as T;
    }

    private static string EscapeJson(string s) {
      if (s == null) return "";
      return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string MakeError(string msg) {
      return $"{{\"error\":\"{EscapeJson(msg)}\"}}";
    }

    private static void SendResponse(HttpListenerResponse resp, int statusCode, string body) {
      try {
        byte[] buffer = Encoding.UTF8.GetBytes(body);
        resp.StatusCode = statusCode;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = buffer.Length;
        resp.OutputStream.Write(buffer, 0, buffer.Length);
        resp.OutputStream.Close();
      } catch { }
    }

    private static void SendHtml(HttpListenerResponse resp, string html) {
      try {
        byte[] buffer = Encoding.UTF8.GetBytes(html);
        resp.StatusCode = 200;
        resp.ContentType = "text/html; charset=utf-8";
        resp.ContentLength64 = buffer.Length;
        resp.OutputStream.Write(buffer, 0, buffer.Length);
        resp.OutputStream.Close();
      } catch { }
    }

    // ── 局域网 IPv4(复用 NicScanner:已连接 + 有可路由 IPv4 的物理网卡) ──
    // 分享候选枚举。虚拟卡(VMware VMnet 等报告为 Ethernet)的 IP 别的设备路由不到,
    // 必须排除; 优先有 IPv4 默认网关的物理网卡。多网关(USB 共享 + Wi-Fi 同连)时顺序不定,
    // 由分享窗口的网络下拉框交给用户选(net481 的 IPv4InterfaceProperties 无 Metric 可排)。
    public static List<(string Name, string Ip)> GetLanIpCandidates() {
      var withGw = new List<(string Name, string Ip)>();
      var noGw = new List<(string Name, string Ip)>();
      try {
        foreach (var nif in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) {
          var t = nif.NetworkInterfaceType;
          if (t != System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211
            && t != System.Net.NetworkInformation.NetworkInterfaceType.Ethernet) continue;
          if (nif.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
          var props = nif.GetIPProperties();
          string ip = null;
          foreach (var a in props.UnicastAddresses) {
            if (a.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            var b = a.Address.GetAddressBytes();
            if (b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254)) continue;
            ip = a.Address.ToString(); break;
          }
          if (ip == null) continue;
          bool hasGw = false;
          foreach (var g in props.GatewayAddresses)
            if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { hasGw = true; break; }
          if (hasGw) {
            withGw.Add((nif.Name, ip));
          } else if (!IsVirtualNicName(nif.Name)) {
            noGw.Add((nif.Name, ip));
          }
        }
      } catch { }
      var result = withGw.Count > 0 ? withGw : noGw;
      return result;
    }

    public static string GetLanIp() {
      var c = GetLanIpCandidates();
      return c.Count > 0 ? c[0].Ip : "127.0.0.1";
    }

    static bool IsVirtualNicName(string name) {
      if (string.IsNullOrEmpty(name)) return false;
      foreach (var key in new[] { "VMware", "VMnet", "VirtualBox", "vEthernet", "Loopback", "Loop Back",
        "TAP", "Tailscale", "Bluetooth", "WSL", "Hyper-V", "WAN Miniport" })
        if (name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return true;
      return false;
    }

    /// <summary>供 UI/二维码使用的分享链接(带令牌)。</summary>
    public static string BuildShareUrl() => BuildShareUrl(GetLanIp());
    public static string BuildShareUrl(string ip) =>
      $"http://{ip}:{Port}/?token={_apiToken ?? ""}";

    // ── 内置 WebUI:启动时从嵌入资源读一次并缓存(Resources/webui.html) ──
    static string _webUiHtml;
    static string WebUiHtml => _webUiHtml ?? (_webUiHtml = LoadWebUiHtml());

    static string LoadWebUiHtml() {
      try {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using (var s = asm.GetManifestResourceStream("OmenSuperHub.Resources.webui.html")) {
          if (s == null) { Logger.Error("webui.html embedded resource not found"); return "<!DOCTYPE html><title>OmenXHub</title><p>WebUI resource missing.</p>"; }
          using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }
      } catch (Exception ex) {
        Logger.Error($"LoadWebUiHtml failed: {ex.Message}");
        return "<!DOCTYPE html><title>OmenXHub</title><p>WebUI load error.</p>";
      }
    }

    // ═══════════════════════════════════════════════════════
    // 自检 — 纯函数断言,不开监听
    // ═══════════════════════════════════════════════════════
    public static string SelfCheck() {
      var sb = new StringBuilder("[HardwareApi] ");
      int fail = 0;
      void Check(string name, bool ok) { if (!ok) { fail++; sb.Append("FAIL:").Append(name).Append(' '); } }

      // LAN 白名单:破坏性写必须拒,监控读/预设/风扇写必须放
      Check("deny-shutdown", !IsLanAllowed("POST", "/api/system/shutdown"));
      Check("deny-restart", !IsLanAllowed("POST", "/api/system/restart"));
      Check("deny-powerlimit", !IsLanAllowed("POST", "/api/power/limit"));
      Check("deny-pl1", !IsLanAllowed("POST", "/api/cpu/pl1"));
      Check("deny-gpuoc", !IsLanAllowed("POST", "/api/gpu/core/offset"));
      Check("deny-tgp", !IsLanAllowed("POST", "/api/gpu/tgp"));
      Check("deny-lightwrite", !IsLanAllowed("POST", "/api/lighting/color"));
      Check("deny-fanclean", !IsLanAllowed("POST", "/api/fan/clean"));
      Check("allow-mode", IsLanAllowed("POST", "/api/mode"));
      // 遥控=只切 FanPage 五档模式; 直写转速/曲线数值/跑满开关全部仅回环。
      Check("allow-fanmode-write", IsLanAllowed("POST", "/api/fan/mode"));
      Check("allow-fanmode-read", IsLanAllowed("GET", "/api/fan/mode"));
      // 内存: 读监控放行, 清理属无损维护动作(WebUI 按钮要用)故放行。
      Check("allow-memory-read", IsLanAllowed("GET", "/api/memory"));
      Check("allow-memoryclean-write", IsLanAllowed("POST", "/api/memory/clean"));
      // 关屏无损可逆(任何输入唤醒), 放行; 重启/关机必须仍拒。
      Check("allow-displayoff-write", IsLanAllowed("POST", "/api/system/display/off"));
      Check("deny-restart-lan", !IsLanAllowed("POST", "/api/system/restart"));
      Check("deny-fancurve-write", !IsLanAllowed("POST", "/api/fan/curve"));
      Check("deny-fanspeed-write", !IsLanAllowed("POST", "/api/fan/speed"));
      Check("deny-fanmax-write", !IsLanAllowed("POST", "/api/fan/max"));
      Check("allow-fanspeed-read", IsLanAllowed("GET", "/api/fan/speed"));
      Check("allow-temp", IsLanAllowed("GET", "/api/temperature"));
      Check("allow-hwall", IsLanAllowed("GET", "/api/hardware/all"));
      Check("allow-ping", IsLanAllowed("GET", "/ping"));
      Check("deny-unknown", !IsLanAllowed("GET", "/api/secret"));

      // JSON 格式串回归:内插串里 "{X:F0}}}" 的 }} 会被并进格式说明符,输出字面量 "F2923"
      // 而非数值(整个端点 JSON 解析失败)。曾静默存在于 5 个端点(令牌无展示入口时全 403
      // 掩盖)。断言每个 handler 输出不含 ":F" 且以 } 收尾。
      void CheckJson(string name, string json) =>
        Check(name, json != null && !json.Contains(":F") && json.TrimEnd().EndsWith("}"));
      CheckJson("json-cpu-freq", HandleGetCpuFrequency());
      CheckJson("json-gpu-load", HandleGetGpuLoad());
      CheckJson("json-gpu-freq", HandleGetGpuFrequency());
      CheckJson("json-gpu-mem", HandleGetGpuMemory());
      // 注意: HandleCleanMemory 会真 trim 全进程工作集, 自检绝不调用它(只测纯函数白名单)。
      CheckJson("json-memory", HandleGetMemory());
      CheckJson("json-hw-all", HandleGetHardwareAll());

      // 同源:回环放行;同 host:port 放行;异站拒
      var reqUrl = new Uri("http://192.168.1.10:5000/api/temperature");
      Check("origin-loopback", IsSameOriginOrLoopback(reqUrl, "http://localhost:5000"));
      Check("origin-same", IsSameOriginOrLoopback(reqUrl, "http://192.168.1.10:5000"));
      Check("origin-evil", !IsSameOriginOrLoopback(reqUrl, "http://evil.com:5000"));
      Check("origin-empty", IsSameOriginOrLoopback(reqUrl, ""));

      // 嵌入 WebUI 资源:必须真读得到(资源名/嵌入配置写错时静默返回占位页)。
      // 断言用结构标记而非品牌字串 —— 品牌名会随 UI 改名失效(2026-10-15 标题改成
      // "Omen X Hub" 时原 "OmenXHub" 字面量断言即误报 FAIL), 结构标记与占位页互斥。
      string ui = WebUiHtml;
      Check("webui-loaded", ui != null && ui.Contains("class=\"title-bar\"")
        && ui.Contains("</html>") && !ui.Contains("WebUI resource missing"));

      // 分享 IP 选卡:虚拟卡名必须被兜底路径排除(曾因取 NicScanner nics[0] 把 VMnet1 写进二维码,
      // 别的设备路由不到)。候选枚举依赖真实网卡,只断言其纯函数部分。
      Check("nic-vmnet-excluded", IsVirtualNicName("VMware Network Adapter VMnet1")
        && IsVirtualNicName("vEthernet (Default Switch)") && IsVirtualNicName("Tailscale"));
      Check("nic-physical-kept", !IsVirtualNicName("WLAN") && !IsVirtualNicName("以太网 2")
        && !IsVirtualNicName("Intel(R) Ethernet Connection"));

      sb.Append(fail == 0 ? "PASS" : $"{fail} FAILED");
      return sb.ToString();
    }

  }
}
