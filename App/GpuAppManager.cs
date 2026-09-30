// GpuAppManager.cs - GPU 进程与超频管理
// 使用 nvidia-smi 和 NvAPIWrapper.Net 查询 GPU 应用、时钟偏移、功耗限制，支持超频调节
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;

namespace OmenSuperHub {
  public static class GpuAppManager {
    public class GpuAppInfo {
      public int ProcessId { get; set; }
      public string ProcessName { get; set; }
      public string FilePath { get; set; }
    }

    public static List<GpuAppInfo> GetGpuApps() {
      var apps = new List<GpuAppInfo>();
      try {
        // ponytail: try --query-compute-apps first, fall back to parsing standard output
        string command = "nvidia-smi --query-compute-apps=pid,process_name --format=csv,noheader";
        var result = Services.TrayService.ExecuteCommand(command);
        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output)) {
          string[] lines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
          foreach (string line in lines) {
            string[] parts = line.Split(',');
            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out int pid)) {
              try { using (var p = Process.GetProcessById(pid)) { } } catch { continue; }
              string fullPath = parts[1].Trim();
              apps.Add(new GpuAppInfo {
                ProcessId = pid,
                ProcessName = System.IO.Path.GetFileName(fullPath),
                FilePath = GetProcessPath(pid, fullPath) ?? fullPath
              });
            }
          }
        }
        if (apps.Count == 0) {
          // fallback: parse standard nvidia-smi Processes section
          result = Services.TrayService.ExecuteCommand("nvidia-smi");
          if (result.ExitCode == 0) {
            var m = Regex.Match(result.Output, @"\|(\s+\d+\s+\S+\s+\S+\s+)(\d+)(\s+)(\S+)(\s+\S[^|]*)\|");
            // simpler: find all "PID" lines
            foreach (Match match in Regex.Matches(result.Output, @"^\|\s+\d+\s+N/A\s+N/A\s+(\d+)\s+\S+\s+(\S[^|]*?)\s+\S+\s*\|", RegexOptions.Multiline)) {
              if (int.TryParse(match.Groups[1].Value, out int pid)) {
                try { using (var p = Process.GetProcessById(pid)) { } } catch { continue; }
                string name = match.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(name) && !apps.Any(a => a.ProcessId == pid))
                  apps.Add(new GpuAppInfo { ProcessId = pid, ProcessName = name, FilePath = GetProcessPath(pid, name) });
              }
            }
          }
        }
      } catch (Exception ex) { Logger.Verbose($"[GetGpuApps] {ex.Message}"); }
      return apps;
    }

    static string GetProcessPath(int pid, string fallbackName) {
      try {
        using (var searcher = new ManagementObjectSearcher($"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {pid}"))
        using (var results = searcher.Get()) {
          foreach (ManagementObject obj in results) {
            using (obj) {
              string path = obj["ExecutablePath"]?.ToString();
              if (!string.IsNullOrEmpty(path)) return path;
            }
          }
        }
      } catch { }
      try {
        using (var proc = Process.GetProcessById(pid)) {
          return proc.MainModule?.FileName;
        }
      } catch { }
      return null;
    }

    public static void RestartGpu() {
      try {
        string instanceId = null;
        string query = "SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'Display'";
        using (var searcher = new ManagementObjectSearcher(query)) {
          foreach (ManagementObject device in searcher.Get()) {
            using (device) {
              string description = device["Description"]?.ToString();
              if (!string.IsNullOrEmpty(description) && description.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0) {
                instanceId = device["PNPDeviceID"]?.ToString();
                break;
              }
            }
          }
        }
        if (string.IsNullOrEmpty(instanceId)) return;
        Services.TrayService.ExecuteCommand($"pnputil /restart-device \"{instanceId}\"");
      } catch (Exception ex) { Logger.Verbose($"[RestartGpu] {ex.Message}"); }
    }

    public static List<string> GetAllGpuNamesList() {
      var gpuNames = new List<string>();
      try {
        using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility FROM Win32_VideoController"))
        using (var collection = searcher.Get()) {
          foreach (ManagementObject obj in collection) {
            using (obj) {
              string name = obj["Name"]?.ToString() ?? "";
              string compatibility = obj["AdapterCompatibility"]?.ToString() ?? "";
              if (name.Contains("Microsoft") || compatibility.Contains("Microsoft")) continue;
              if (name.Contains("Display")) continue;
              if (!string.IsNullOrWhiteSpace(name)) gpuNames.Add(name.Trim());
            }
          }
        }
      } catch (Exception ex) { Logger.Verbose($"[GetAllGpuNamesList] {ex.Message}"); }
      return gpuNames;
    }

    public static float[] GetGpuPowerLimits() {
      var limits = new float[2] { -2f, -2f };
      try {
        var result = Services.TrayService.ExecuteCommand("nvidia-smi -q -d POWER");
        if (result.ExitCode == 0) {
          string currentPattern = @"Current Power Limit\s+:\s+([\d.]+)\s+W";
          string maxPattern = @"Max Power Limit\s+:\s+([\d.]+)\s+W";
          var currentMatch = Regex.Match(result.Output, currentPattern);
          var maxMatch = Regex.Match(result.Output, maxPattern);
          if (currentMatch.Success && maxMatch.Success) {
            // ponytail: nvidia-smi 输出恒用 '.' 作小数点——必须 InvariantCulture。
            // 逗号小数区(fr-FR 等)下区域敏感解析抛异常被吞→功耗行静默不显示;
            // '.' 为千分位区(de-DE/tr-TR 等)下 115.00 被静默误读成 11500 并持久化。
            limits[0] = float.Parse(currentMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            limits[1] = float.Parse(maxMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
          }
        }
      } catch (Exception ex) { Logger.Verbose($"[GetGpuPowerLimits] {ex.Message}"); }
      return limits;
    }

    public static int GetGpuTemperatureTarget() {
      int limit = -2;
      try {
        var result = Services.TrayService.ExecuteCommand("nvidia-smi -q -d TEMPERATURE");
        if (result.ExitCode == 0) {
          // ponytail: 新驱动(RTX 50 系)字段改名为 "GPU Target Temperature Specification",
          // 旧驱动无 Specification 后缀——后缀做可选匹配,两代驱动通吃。
          string targetPattern = @"GPU Target Temperature(?: Specification)?\s+:\s+(\d+)\s+C";
          var targetMatch = Regex.Match(result.Output, targetPattern);
          if (targetMatch.Success) limit = int.Parse(targetMatch.Groups[1].Value);
        }
      } catch (Exception ex) { Logger.Verbose($"[GetGpuTemperatureTarget] {ex.Message}"); }
      return limit;
    }

    public static bool CheckDBVersion(int kind) {
      var result = Services.TrayService.ExecuteCommand("nvidia-smi");
      if (result.ExitCode == 0) {
        string pattern = @"NVIDIA-SMI\s+(\d+\.\d+)";
        Match match = Regex.Match(result.Output, pattern);
        string version = match.Success ? match.Groups[1].Value : null;
        if (version != null) {
          Version v1 = new Version(version);
          Version v2 = new Version("537.42");
          Version v3 = new Version("610.47");
          if (v1 >= v2 && v1 < v3) return true;
        }
      }
      return false;
    }

    public static void SetCoreClockOffset(int offsetMhz) {
      // R15/BUG-D2: NVIDIA.Initialize()/GetPerformanceStates20 在无 N 卡/驱动异常时抛
      // NVIDIAResultException。旧实现 Initialize 在 try 之外,调用方(PerfPage 线程池回调)
      // 又裸调,异常直达线程池 → .NET 4.8 默认策略直接终止进程。库边界统一吞并记日志,
      // 让所有调用方(UI 线程池 / HTTP API)安全。
      try {
        NVIDIA.Initialize();
        try {
          PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
          if (gpus.Length == 0) return;
          PhysicalGPU gpu = gpus[0];
          var clockDelta = new PerformanceStates20ClockEntryV1(
              PublicClockDomain.Graphics,
              new PerformanceStates20ParameterDelta(offsetMhz * 1000));
          var pState = new PerformanceStates20InfoV1.PerformanceState20(
              PerformanceStateId.P0_3DPerformance,
              new PerformanceStates20ClockEntryV1[] { clockDelta },
              new PerformanceStates20BaseVoltageEntryV1[0]);
          var writeInfo = new PerformanceStates20InfoV1(
              new PerformanceStates20InfoV1.PerformanceState20[] { pState },
              1u, 0u);
          GPUApi.SetPerformanceStates20(gpu.Handle, writeInfo);
        } finally {
          NVIDIA.Unload();
        }
      } catch (Exception ex) { Logger.Verbose($"[SetCoreClockOffset] {ex.Message}"); }
    }

    public static void SetMemoryClockOffset(int offsetMhz) {
      // R15/BUG-D2: 同 SetCoreClockOffset —— 库边界兜底 NVAPI 异常。
      try {
        NVIDIA.Initialize();
        try {
          PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
          if (gpus.Length == 0) return;
          PhysicalGPU gpu = gpus[0];
          var clockDelta = new PerformanceStates20ClockEntryV1(
              PublicClockDomain.Memory,
              new PerformanceStates20ParameterDelta(offsetMhz * 1000));
          var pState = new PerformanceStates20InfoV1.PerformanceState20(
              PerformanceStateId.P0_3DPerformance,
              new PerformanceStates20ClockEntryV1[] { clockDelta },
              new PerformanceStates20BaseVoltageEntryV1[0]);
          var writeInfo = new PerformanceStates20InfoV1(
              new PerformanceStates20InfoV1.PerformanceState20[] { pState },
              1u, 0u);
          GPUApi.SetPerformanceStates20(gpu.Handle, writeInfo);
        } finally {
          NVIDIA.Unload();
        }
      } catch (Exception ex) { Logger.Verbose($"[SetMemoryClockOffset] {ex.Message}"); }
    }

    public static int GetCoreClockOffset() {
      // R15/BUG-D2 衍生: 无 GPU 时 GetPhysicalGPUs()[0] 越界,Initialize 亦在 try 外。
      try {
        NVIDIA.Initialize();
        try {
          PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
          if (gpus.Length == 0) return 0;
          PhysicalGPU gpu = gpus[0];
          var pstatesInfo = GPUApi.GetPerformanceStates20(gpu.Handle);
          if (pstatesInfo.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var clockEntries)) {
            foreach (var clock in clockEntries) {
              if (clock.DomainId == PublicClockDomain.Graphics) {
                return clock.FrequencyDeltaInkHz.DeltaValue / 1000;
              }
            }
          }
          return 0;
        } finally {
          NVIDIA.Unload();
        }
      } catch (Exception ex) { Logger.Verbose($"[GetCoreClockOffset] {ex.Message}"); return 0; }
    }

    public static int GetMemoryClockOffset() {
      // R15/BUG-D2 衍生: 同 GetCoreClockOffset —— 库边界兜底 + 空数组防护。
      try {
        NVIDIA.Initialize();
        try {
          PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
          if (gpus.Length == 0) return 0;
          PhysicalGPU gpu = gpus[0];
          var pstatesInfo = GPUApi.GetPerformanceStates20(gpu.Handle);
          if (pstatesInfo.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var clockEntries)) {
            foreach (var clock in clockEntries) {
              if (clock.DomainId == PublicClockDomain.Memory) {
                return clock.FrequencyDeltaInkHz.DeltaValue / 1000;
              }
            }
          }
          return 0;
        } finally {
          NVIDIA.Unload();
        }
      } catch (Exception ex) { Logger.Verbose($"[GetMemoryClockOffset] {ex.Message}"); return 0; }
    }

    // ─── NVIDIA 官方温度源 ───
    public static bool TryGetNvApiTemperature(string preferredGpuName, out float celsius) {
      celsius = 0;
      try {
        NVIDIA.Initialize();
        foreach (PhysicalGPU gpu in PhysicalGPU.GetPhysicalGPUs()) {
          if (!string.IsNullOrWhiteSpace(preferredGpuName)
              && preferredGpuName.IndexOf(gpu.FullName, StringComparison.OrdinalIgnoreCase) < 0
              && gpu.FullName.IndexOf(preferredGpuName, StringComparison.OrdinalIgnoreCase) < 0)
            continue;
          var sensor = gpu.ThermalInformation.ThermalSensors
            .FirstOrDefault(s => s.Target == ThermalSettingsTarget.GPU);
          int temp = sensor?.CurrentTemperature ?? 0;
          if (temp >= 15 && temp <= 120) { celsius = temp; return true; }
        }
        return false;
      } catch { return false; }
      finally { try { NVIDIA.Unload(); } catch { } }
    }

    public static bool TryGetNvmlTemperature(string preferredGpuName, out float celsius) {
      celsius = 0;
      try {
        if (!Nvml.TryGetGpu(preferredGpuName, out IntPtr gpu)) return false;
        if (Nvml.nvmlDeviceGetTemperature(gpu, 0, out uint temp) != Nvml.SUCCESS || temp < 15 || temp > 120)
          return false;
        celsius = temp;
        return true;
      } catch { return false; }
    }

    // Direct NVML P/Invoke (no CLI parsing, instant apply)
    public static bool SetPowerLimit(int watts) {
      try {
        if (!Nvml.TryGetGpu(out IntPtr gpu)) return false;
        if (!TryGetPowerLimitInfo(out var info)) return false;
        if (watts < info.Min || watts > info.Max) return false;
        return Nvml.nvmlDeviceSetPowerManagementLimit(gpu, (uint)watts * 1000) == 0;
      } catch { return false; }
    }

    public static bool TryGetPowerLimitInfo(out (int Min, int Current, int Default, int Max) info) {
      info = default;
      try {
        if (!Nvml.TryGetGpu(out IntPtr gpu)) return false;
        int r1 = Nvml.nvmlDeviceGetPowerManagementLimitConstraints(gpu, out uint minMw, out uint maxMw);
        int r2 = Nvml.nvmlDeviceGetPowerManagementLimit(gpu, out uint curMw);
        int r3 = Nvml.nvmlDeviceGetPowerManagementDefaultLimit(gpu, out uint defMw);
        if (r1 != 0 || r2 != 0 || r3 != 0) return false;
        info = ((int)(minMw / 1000), (int)(curMw / 1000), (int)(defMw / 1000), (int)(maxMw / 1000));
        return true;
      } catch { return false; }
    }

    // ponytail: V-F 曲线死代码已删除
    //   删除项：VfPoint/VfPointCount/VoltageStepMv/RampStartMv/VfToleranceMHz 常量、
    //   _vfApiInited/_vfGetStatusPtr/_vfSetControlPtr 字段、InitVfApi/ReadLe32/WriteLe32、
    //   TryGetVfCurve/SetVfPointOffset/SetVoltageCurveOffset/SetUndervoltCurveFromDefault、
    //   CalculateDesiredPoint/AlignTo25Mv/AlignToSupportedVoltage/PreviewDesiredCurve、
    //   VerifyUndervoltCurve/ResetVfCurve/ApplyVfCurveFromUserEdits、
    //   GetNvidiaGpuInfoList/IsAbove50Series/GetGpuVRAM、
    //   GetGraphicsBoostClock/GetMemoryBoostClock、
    //   SetMaxGpuClock/GetMaxGpuClockLock、NvApiPrivate 嵌套类
    //   外部零调用，全为 V-F 降压子系统残留

    // ─── NVML P/Invoke wrapper (like UXTU) ───
    static class Nvml {
      public const string Dll = "nvml.dll";
      public const int SUCCESS = 0;
      static bool _init;
      static bool _ok;
      public static bool EnsureInit() {
        if (_init) return _ok;
        _init = true;
        _ok = nvmlInit_v2() == SUCCESS;
        return _ok;
      }
      public static bool TryGetGpu(out IntPtr gpu) => TryGetGpu(null, out gpu);
      public static bool TryGetGpu(string preferredName, out IntPtr gpu) {
        gpu = IntPtr.Zero;
        if (!EnsureInit() || nvmlDeviceGetCount_v2(out uint count) != SUCCESS || count == 0) return false;
        for (uint i = 0; i < count; i++) {
          if (nvmlDeviceGetHandleByIndex_v2(i, out IntPtr candidate) != SUCCESS) continue;
          if (string.IsNullOrWhiteSpace(preferredName)) { gpu = candidate; return true; }
          var name = new System.Text.StringBuilder(128);
          if (nvmlDeviceGetName(candidate, name, (uint)name.Capacity) == SUCCESS
              && (preferredName.IndexOf(name.ToString(), StringComparison.OrdinalIgnoreCase) >= 0
                  || name.ToString().IndexOf(preferredName, StringComparison.OrdinalIgnoreCase) >= 0)) {
            gpu = candidate;
            return true;
          }
        }
        return false;
      }
      [DllImport(Dll)] public static extern int nvmlInit_v2();
      [DllImport(Dll)] public static extern int nvmlShutdown();
      [DllImport(Dll)] public static extern int nvmlDeviceGetCount_v2(out uint count);
      [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
      [DllImport(Dll)] public static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);
      [DllImport(Dll)] public static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limitMw);
      [DllImport(Dll)] public static extern int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint limitMw);
      [DllImport(Dll)] public static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minMw, out uint maxMw);
      [DllImport(Dll)] public static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint limitMw);
      [DllImport(Dll)] public static extern int nvmlDeviceSetGpuLockedClocks(IntPtr device, uint minGpuMHz, uint maxGpuMHz);
      [DllImport(Dll)] public static extern int nvmlDeviceResetGpuLockedClocks(IntPtr device);
      [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int nvmlDeviceGetName(IntPtr device, System.Text.StringBuilder name, uint length);
    }

  }
}
