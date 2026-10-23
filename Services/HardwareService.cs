// HardwareService.cs - 硬件监控服务
// LibreHardwareMonitor 集成，CPU/GPU 温度/功耗/利用率/时钟轮询，GPU 自动启停
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using OmenSuperHub.Pages;
using LibreComputer = LibreHardwareMonitor.Hardware.Computer;
using LibreIHardware = LibreHardwareMonitor.Hardware.IHardware;
using LibreHardwareType = LibreHardwareMonitor.Hardware.HardwareType;
using LibreISensor = LibreHardwareMonitor.Hardware.ISensor;
using LibreSensorType = LibreHardwareMonitor.Hardware.SensorType;

namespace OmenSuperHub.Services {
  internal static class HardwareService {
    static readonly object _lock = new object();
    static DateTime _lastQueryTime = DateTime.MinValue;
    static readonly TimeSpan _cacheInterval = TimeSpan.FromMilliseconds(800);

    // ═══════════════════════════════════════════════════════
    // Hardware State (thread-safe)
    // ═══════════════════════════════════════════════════════
    static float _cpuTemp = 50;
    public static float CPUTemp { get { lock (_lock) return _cpuTemp; } set { lock (_lock) _cpuTemp = value; } }
    // ponytail: IR(红外)温度 — 经 OMEN WMI 0x23 通道读,参与官方三路风扇 max(cpu,gpu,ir)。
    // 负值=未读到(该机型无 IR 传感器),风扇侧 max 时天然被忽略,行为与未支持机型一致。
    static float _irTemp = -1;
    public static float IrTemp { get { lock (_lock) return _irTemp; } set { lock (_lock) _irTemp = value; } }
    static float _gpuTemp = 40;
    public static float GPUTemp { get { lock (_lock) return _gpuTemp; } set { lock (_lock) _gpuTemp = value; } }
    static float _cpuPower = 0;
    public static float CPUPower { get { lock (_lock) return _cpuPower; } set { lock (_lock) _cpuPower = value; } }
    static float _gpuPower = 0;
    public static float GPUPower { get { lock (_lock) return _gpuPower; } set { lock (_lock) _gpuPower = value; } }
    static float _cpuUsage = 0;
    public static float CPUUsage { get { lock (_lock) return _cpuUsage; } set { lock (_lock) _cpuUsage = value; } }
    static float _gpuUsage = 0;
    public static float GPUUsage { get { lock (_lock) return _gpuUsage; } set { lock (_lock) _gpuUsage = value; } }
    static float _cpuClock = 0;
    public static float CPUClock { get { lock (_lock) return _cpuClock; } set { lock (_lock) _cpuClock = value; } }
    static float _gpuClock = 0;
    public static float GPUClock { get { lock (_lock) return _gpuClock; } set { lock (_lock) _gpuClock = value; } }
    public static float RespondSpeed = 0.4f;
    public static bool MonitorCPU = true;
    public static bool MonitorGPU = true;
    public static bool MonitorFan = true;
    public static bool IsConnectedToNVIDIA = true;
    static bool _powerOnline = true;
    public static bool PowerOnline { get { lock (_lock) return _powerOnline; } set { lock (_lock) _powerOnline = value; } }
    // ponytail: -1 确保首次风扇定时器 tick 必定执行写入, 不与真实速度值冲突。
    // 3 维: [0]=CPU [1]=GPU [2]=第三扇(仅三扇机型;双风机恒为 -1,UI 按 IsThreeFan 决定是否显示)。
    static readonly int[] _fanSpeedNow = new int[3] { -1, -1, -1 };
    public static IReadOnlyList<int> FanSpeedNow => _fanSpeedNow;  // direct ref, no allocation per access
    public static void UpdateFanSpeed(IReadOnlyList<int> values) {
      if (values == null || values.Count < 2) return;
      lock (_lock) {
        _fanSpeedNow[0] = values[0];
        _fanSpeedNow[1] = values[1];
        if (values.Count >= 3) _fanSpeedNow[2] = values[2];
      }
    }
    public static bool IsAmbientSensorSupported;
    public static string PawnIOState = "";

    // Internal state
    // ponytail: Storage/Motherboard group 启动时常开,不按勾选动态开/关 LHM Computer。代价是这两个 group
    // 进入 800ms 轮询(每 group 1-3 个传感器,微秒级),避免 Open/Close 动态增删 group 的复杂度。
    public static LibreComputer LibreComputer = new LibreComputer() {
      IsCpuEnabled = true, IsGpuEnabled = true,
      IsStorageEnabled = true, IsMotherboardEnabled = true,
    };

    // ═══ 额外温度传感器 — 固定候选清单(稳定唯一 ID;UI 显示名见 App/Strings.cs 的 SysGpuHotSpot 等) ═══
    // ponytail: 固定清单不复用 OMEN WMI 0x23 那路(IR/Ambient/PCH/VR 已在 Dashboard 独立显示,纳入本管理会双重)
    public static readonly string[] ExtraSensorIds = {
      "GPUNV_HOTSPOT", "CPU_COREMAX", "CPU_COREAVG", "CPU_TJMAX_DISTANCE",
      "STORAGE_NVME_0", "STORAGE_NVME_1", "MOTHERBOARD_SUPERIO",
    };
    // ID → 当前温度(未读到=负数)。每轮 QueryHardware 按"读到才覆写"语义,读不到保留上次值(避免 UI 抖动)。
    // ponytail: _extraRaw/_extraSmoothed 专用锁 —— 线程池写(QueryHardware)对 UI 读
    // (FloatingWindow/Dashboard/TrayPopup 250ms~1s 轮询)。绝不能罩进上面的 _lock: 那是
    // CPUTemp/GPUPower 等标量 getter 锁, 把 QueryHardware 的写段(含 hardware.Update() 的
    // PawnIO/EC/NVAPI/WMI 实 I/O)挂上去 = 拿 UI 停帧换一个竞态。锁粒度只到字典操作本身。
    static readonly object _extraLock = new object();
    static readonly Dictionary<string, float> _extraRaw = new();
    static readonly Dictionary<string, float> _extraSmoothed = new();   // EMA 平滑副本
    // ponytail: 标记本轮读到与否,只用于"未读到"时刷新逻辑(同上)。仅 QueryHardware 线程自写自读,不入围。
    static readonly Dictionary<string, bool> _extraSeenThisTick = new();
    public static IReadOnlyDictionary<string, float> ExtraTemps => _extraSmoothed;
    // ponytail: 读不到值返回负数,UI 借此显 "-"。1~120°C 钳位与 CPU/GPU 同口径。
    public static float GetDisplayExtraTemp(string id) {
      lock (_extraLock) {
        if (!_extraRaw.TryGetValue(id, out float raw) || raw < 1f || raw > 120f) return -1;
        return _extraSmoothed.TryGetValue(id, out float sm) ? sm : raw;
      }
    }

    // ponytail: 机型是否真有该传感器——_extraRaw 仅在有效读到时写入,ContainsKey 即"曾读到"。
    // UI 借此隐藏本机不存在的行(如笔记本的 SuperIO 主板温度);临时丢读不隐藏(值仍保留)。
    public static bool HasExtraTemp(string id) { lock (_extraLock) return _extraRaw.ContainsKey(id); }

    // ═══ GPU 选择 — 用户在设置页选指定 GPU 显示其温度/利用率/功耗/时钟 ═══
    // ponytail: 空 SelectedGpu 按 NVIDIA → AMD → Intel 选一个；非空优先匹配稳定 Identifier。
    static bool IsGpu(LibreIHardware h) => h.HardwareType == LibreHardwareType.GpuNvidia
      || h.HardwareType == LibreHardwareType.GpuAmd || h.HardwareType == LibreHardwareType.GpuIntel;
    static LibreIHardware ResolveTargetGpu(IEnumerable<LibreIHardware> hardware) {
      var gpus = hardware.Where(IsGpu).ToList();
      string sel = ConfigService.SelectedGpu ?? "";
      if (!string.IsNullOrWhiteSpace(sel))
        return gpus.FirstOrDefault(h => h.Identifier.ToString() == sel)
          ?? gpus.FirstOrDefault(h => h.Name == sel);
      return gpus.FirstOrDefault(h => h.HardwareType == LibreHardwareType.GpuNvidia)
        ?? gpus.FirstOrDefault(h => h.HardwareType == LibreHardwareType.GpuAmd)
        ?? gpus.FirstOrDefault(h => h.HardwareType == LibreHardwareType.GpuIntel);
    }
    // 列出可用 GPU(供设置页 ComboBox 枚举)。启动 LibreComputer.Open 后才有效。
    public static List<(string Id, string Name, string Vendor)> GetAvailableGpus() {
      var list = new List<(string, string, string)>();
      try {
        foreach (LibreIHardware h in LibreComputer.Hardware) {
          string v = h.HardwareType == LibreHardwareType.GpuNvidia ? "NVIDIA"
                   : h.HardwareType == LibreHardwareType.GpuAmd ? "AMD"
                   : h.HardwareType == LibreHardwareType.GpuIntel ? "Intel" : null;
          if (v != null) list.Add((h.Identifier.ToString(), h.Name, v));
        }
      } catch { }
      return list;
    }

    static int countQuery = 0;
    public static bool AutoStartMonitorGPU = true, AutoStopMonitorGPU = true;

    // ═══════════════════════════════════════════════════════
    // Display device detection (for GPU connection check)
    // struct/PInvoke 复用 Pages/NativeMethods.cs 的 NativeMethods_Display
    // ═══════════════════════════════════════════════════════
    [Flags()]
    enum DisplayDeviceStateFlags : int {
      AttachedToDesktop = 0x1,
      MultiDriver = 0x2,
      PrimaryDevice = 0x4,
      MirroringDriver = 0x8,
      VGACompatible = 0x10,
      Removable = 0x20,
      ModesPruned = 0x8000000,
      Remote = 0x4000000,
      Disconnect = 0x2000000
    }

    public static void MonitorQuery() {
      var d = new NativeMethods_Display.DISPLAY_DEVICE();
      d.cb = Marshal.SizeOf(d);
      uint deviceNum = 0;
      bool hasAttachedNvidia = false;

      while (NativeMethods_Display.EnumDisplayDevices(null, deviceNum, ref d, 0)) {
        if (((DisplayDeviceStateFlags)d.StateFlags).HasFlag(DisplayDeviceStateFlags.AttachedToDesktop)
            && d.DeviceString.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
          hasAttachedNvidia = true;
        deviceNum++;
      }

      IsConnectedToNVIDIA = hasAttachedNvidia;
    }

    public static void DetectAmbientSensor() {
      int irTemp = OmenHardware.GetSensorTemperature(0);
      int ambientTemp = OmenHardware.GetSensorTemperature(1);
      IsAmbientSensorSupported = ambientTemp > 1 && irTemp != ambientTemp;
    }

    public static void RefreshPawnIOState() {
      if (OmenHardware.IsPawnIOInstalled())
        PawnIOState = OmenHardware.GetPawnIOState();
      else
        PawnIOState = "Not Installed";
    }

    // ═══════════════════════════════════════════════════════
    // Hardware Query
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Event raised when GPU monitoring state changes automatically.
    /// Args: (bool gpuEnabled, string message)
    /// </summary>
    public static event Action<bool, string> OnGpuMonitoringChanged;
    public enum GpuTemperatureSource { None = 0, HWiNFO = 1, NvidiaNvml = 2, LibreHardwareMonitor = 3, NvidiaNvApi = 4 }
    static DateTime _lastGpuTempSampleUtc = DateTime.MinValue;
    static DateTime _lastGpuPowerSampleUtc = DateTime.MinValue;
    static GpuTemperatureSource _gpuTempSource;
    static string _lastResolvedGpuId;
    public static bool GpuTargetAvailable { get; private set; }
    public static bool GpuTempFresh => DateTime.UtcNow - _lastGpuTempSampleUtc <= TimeSpan.FromSeconds(3);
    public static bool GpuPowerFresh => DateTime.UtcNow - _lastGpuPowerSampleUtc <= TimeSpan.FromSeconds(3);
    public static bool GpuPowerUsable => MonitorGPU && GpuTargetAvailable && GpuPowerFresh;

    public static bool UpdateGpuTempSample(float value, GpuTemperatureSource source = GpuTemperatureSource.LibreHardwareMonitor) {
      if (float.IsNaN(value) || float.IsInfinity(value) || value < 15 || value > 120) return false;
      // 高优先级源新鲜时拒绝低优先级抢写；失效 3s 后下一级可自动接管。
      if (source < _gpuTempSource && GpuTempFresh) return false;
      _rawGpuTemp = value;
      GPUTemp = value * RespondSpeed + GPUTemp * (1.0f - RespondSpeed);
      _lastGpuTempSampleUtc = DateTime.UtcNow;
      _gpuTempSource = source;
      return true;
    }

    public static bool UpdateGpuPowerSample(float value) {
      if (float.IsNaN(value) || float.IsInfinity(value) || value < 0) return false;
      GPUPower = (int)(value * 10) == 5900 ? 0 : value;
      _gpuPowerDisplay = GPUPower * RespondSpeed + _gpuPowerDisplay * (1.0f - RespondSpeed);
      _lastGpuPowerSampleUtc = DateTime.UtcNow;
      return true;
    }

    public static bool TryGetFreshGpuTemp(out float value) {
      value = GPUTemp;
      return MonitorGPU && GpuTargetAvailable && GpuTempFresh;
    }

    public static float GetEffectiveGpuTemp(float fallback) => TryGetFreshGpuTemp(out float value) ? value : fallback;

    public static void InvalidateGpuSamples() {
      _lastGpuTempSampleUtc = DateTime.MinValue;
      _lastGpuPowerSampleUtc = DateTime.MinValue;
      _gpuTempSource = GpuTemperatureSource.None;
      GpuTargetAvailable = false;
      GPUUsage = 0;
      GPUClock = 0;
      _gpuPowerDisplay = 0;
    }

    public static void QueryHardware() {
      // ponytail: 用 UtcNow 而非 Now —— 与同文件 GpuTempFresh/GpuPowerFresh(188/189) 及
      // TrayService 的 UtcNow 新鲜度判断一致;DateTime.Now 在夏令时切换/系统时钟回拨时
      // 差值为负,会永久满足 < _cacheInterval 导致 QueryHardware 冻结(硬件监控停更且无日志)。
      if ((DateTime.UtcNow - _lastQueryTime) < _cacheInterval) return;

      _lastQueryTime = DateTime.UtcNow;
      // ponytail: 不每轮 Clear —— 改"读到了就覆盖,读不到保留上次值",避免 LHM 间歇读不到
      // (尤其 Intel Core Max/Distance to TjMax 启动初期读不到)导致 UI 抖动出 "-"
      foreach (var id in ExtraSensorIds) _extraSeenThisTick[id] = false;
      // ponytail: 本 tick 的额外温度 raw 先落局部字典, foreach 结束后一次性入 _extraLock 提交 ——
      // UI 侧(HasExtraTemp/GetDisplayExtraTemp)不会撞见半写的字典(撕裂读/脏值)。局部字典只有本线程
      // 可见(QueryHardware 唯一调用方 TrayService.UpdateTooltip 有 Interlocked 重入守卫,串行化)。
      // 代价: 每 tick(≥800ms 一轮)一个小字典。
      var pendingExtra = new Dictionary<string, float>();

      // ponytail: HWiNFO Read 启用时，跳过 LibreHardwareMonitor 传感器轮询及后续覆盖
      float libreTempCPU = -300;
      float librePowerCPU = -1;
      // ponytail: per-snapshot max so CPUClock/GPUClock reflect current clock, not historical peak.
      float snapCpuClock = 0;
      float snapGpuClock = 0;
      LibreIHardware targetGpu = ResolveTargetGpu(LibreComputer.Hardware);
      string targetGpuId = targetGpu?.Identifier.ToString();
      if (!string.Equals(_lastResolvedGpuId, targetGpuId, StringComparison.Ordinal)) {
        InvalidateGpuSamples();
        _lastResolvedGpuId = targetGpuId;
      }
      GpuTargetAvailable = targetGpu != null;
      // NVIDIA 温度源：官方 NVAPI > LHM > 官方 NVML > HWiNFO。
      // LHM 的 GPU Core 温度虽通常也来自 NVAPI，仍作为项目既有采样层单独保留。
      if (MonitorGPU && targetGpu?.HardwareType == LibreHardwareType.GpuNvidia) {
        bool resolved = GpuAppManager.TryGetNvApiTemperature(targetGpu.Name, out float nvapiTemp)
          && UpdateGpuTempSample(nvapiTemp, GpuTemperatureSource.NvidiaNvApi);
        if (!resolved) {
          targetGpu.Update();
          LibreISensor coreTemp = targetGpu.Sensors.FirstOrDefault(s => s.SensorType == LibreSensorType.Temperature && s.Name == "GPU Core");
          resolved = coreTemp?.Value != null
            && UpdateGpuTempSample(coreTemp.Value.Value, GpuTemperatureSource.LibreHardwareMonitor);
        }
        if (!resolved && GpuAppManager.TryGetNvmlTemperature(targetGpu.Name, out float nvmlTemp))
          UpdateGpuTempSample(nvmlTemp, GpuTemperatureSource.NvidiaNvml);
      }
      if (ConfigService.HWiNFOReadEnabled) {
        // HWiNFO 后台读取；前三层新鲜时其温度写入会被优先级守卫拒绝。
        goto afterLibre;
      }

      foreach (LibreIHardware hardware in LibreComputer.Hardware) {
        if (hardware.HardwareType == LibreHardwareType.Cpu || hardware.HardwareType == LibreHardwareType.GpuNvidia || hardware.HardwareType == LibreHardwareType.GpuAmd || hardware.HardwareType == LibreHardwareType.GpuIntel || hardware.HardwareType == LibreHardwareType.Storage || hardware.HardwareType == LibreHardwareType.Motherboard || hardware.HardwareType == LibreHardwareType.SuperIO) {
          hardware.Update();

          foreach (LibreISensor sensor in hardware.Sensors) {
            if (hardware.HardwareType == LibreHardwareType.Cpu) {
              // ponytail: Intel → "CPU Package", AMD → "Package" / "Core (Tctl/Tdie)" / "Core (Tdie)"
              if (sensor.SensorType == LibreSensorType.Temperature &&
                  (sensor.Name.Contains("Package") || sensor.Name.Contains("Tctl/Tdie") || sensor.Name.Contains("Tdie"))) {
                libreTempCPU = (int)sensor.Value.GetValueOrDefault();
              }
              // CPU 额外项 — Intel: CPU Core Max / CPU Core Average / Distance to TjMax;AMD 双 CCD 命名不同(Tdie/CCD1)留待以后
              // ponytail: 1~120°C 钳位;读到才覆写,读不到保留上次 EMA(避免 LHM 间歇读 null 时 UI 抖 "-")
              if (sensor.SensorType == LibreSensorType.Temperature && sensor.Value != null) {
                int v = (int)sensor.Value.GetValueOrDefault();
                if (v >= 1 && v <= 120) {
                  if (sensor.Name == "Core Max")         { pendingExtra["CPU_COREMAX"] = v; _extraSeenThisTick["CPU_COREMAX"] = true; }
                  if (sensor.Name == "Core Average")    { pendingExtra["CPU_COREAVG"] = v; _extraSeenThisTick["CPU_COREAVG"] = true; }
                  // ponytail: LHM 的距离传感器是每核一条 "Core #N Distance to TjMax"(IntelCpu.cs:395)，
                  // 精确名永远匹配不到导致 UI 恒为 "-"。距离 = TjMax − 温度，取最小 = 最热核，
                  // 与"CPU 核心最高"同语义；每 tick 首见直接覆写，避免跨 tick 粘滞旧值。
                  if (sensor.Name.EndsWith("Distance to TjMax")) {
                    // 本 tick 的累计最小值在 pendingExtra 里(与 _extraSeenThisTick 同生), 提交在循环尾
                    pendingExtra["CPU_TJMAX_DISTANCE"] = pendingExtra.TryGetValue("CPU_TJMAX_DISTANCE", out float prevDist)
                        ? Math.Min(prevDist, v) : v;
                    _extraSeenThisTick["CPU_TJMAX_DISTANCE"] = true;
                  }
                }
              }
              if (sensor.SensorType == LibreSensorType.Power && sensor.Name.Contains("Package")) {
                librePowerCPU = sensor.Value.GetValueOrDefault();
              }
              if (sensor.SensorType == LibreSensorType.Load && sensor.Name == "CPU Total") {
                CPUUsage = (float)sensor.Value.GetValueOrDefault();
              }
              if (sensor.SensorType == LibreSensorType.Clock) {
                float v = (float)sensor.Value.GetValueOrDefault();
                if (v > snapCpuClock) snapCpuClock = v;
              }
            } else if (MonitorGPU && hardware.HardwareType == LibreHardwareType.GpuNvidia && ReferenceEquals(hardware, targetGpu)) {
              if (sensor.Name == "GPU Core" && sensor.SensorType == LibreSensorType.Temperature) {
                UpdateGpuTempSample(sensor.Value.GetValueOrDefault());
              }
              // GPU Hot Spot(nVIDIA 命中 "GPU Hot Spot")
              if (sensor.SensorType == LibreSensorType.Temperature && sensor.Name.Contains("Hot Spot")) {
                int v = (int)sensor.Value.GetValueOrDefault();
                if (v >= 1 && v <= 120) { pendingExtra["GPUNV_HOTSPOT"] = v; _extraSeenThisTick["GPUNV_HOTSPOT"] = true; }
              }
              if (sensor.Name == "GPU Package" && sensor.SensorType == LibreSensorType.Power) {
                UpdateGpuPowerSample(sensor.Value.GetValueOrDefault());
              }
              if (sensor.SensorType == LibreSensorType.Load && sensor.Name == "GPU Core") {
                GPUUsage = (float)sensor.Value.GetValueOrDefault();
              }
              if (sensor.SensorType == LibreSensorType.Clock && (sensor.Name == "GPU Core" || sensor.Name.Contains("Core"))) {
                float v = (float)sensor.Value.GetValueOrDefault();
                if (v > snapGpuClock) snapGpuClock = v;
              }
            } else if (MonitorGPU && hardware.HardwareType == LibreHardwareType.GpuAmd && ReferenceEquals(hardware, targetGpu)) {
              if (sensor.Name == "GPU Core" && sensor.SensorType == LibreSensorType.Temperature) {
                UpdateGpuTempSample(sensor.Value.GetValueOrDefault());
              }
              // GPU Hot Spot(AMD 命中 "Hot Spot" 或 "Temperature #2",两者都认)
              if (sensor.SensorType == LibreSensorType.Temperature && (sensor.Name.Contains("Hot Spot") || sensor.Name.Contains("Hotspot"))) {
                int v = (int)sensor.Value.GetValueOrDefault();
                if (v >= 1 && v <= 120) { pendingExtra["GPUNV_HOTSPOT"] = v; _extraSeenThisTick["GPUNV_HOTSPOT"] = true; }
              }
              if (sensor.Name == "GPU Package" && sensor.SensorType == LibreSensorType.Power) {
                UpdateGpuPowerSample(sensor.Value.GetValueOrDefault());
              }
              if (sensor.SensorType == LibreSensorType.Load && sensor.Name == "GPU Core") {
                GPUUsage = (float)sensor.Value.GetValueOrDefault();
              }
              if (sensor.SensorType == LibreSensorType.Clock && (sensor.Name == "GPU Core" || sensor.Name.Contains("Core"))) {
                float v = (float)sensor.Value.GetValueOrDefault();
                if (v > snapGpuClock) snapGpuClock = v;
              }
            } else if (MonitorGPU && hardware.HardwareType == LibreHardwareType.GpuIntel && ReferenceEquals(hardware, targetGpu)) {
              if (sensor.SensorType == LibreSensorType.Load) {
                float val = (float)sensor.Value.GetValueOrDefault();
                if (val > GPUUsage) GPUUsage = val;
              }
            } else if (hardware.HardwareType == LibreHardwareType.Storage) {
              // ponytail: 多盘只取第一个 hardware(符合"固定清单"本意,多盘留待以后)。
              // NVMe 通常报 3 路温度:Temperature / Temperature 1 是同一复合值(实测完全相等),
              // Temperature 2 是另一路(实测 48~58℃ 随负载动,明显高于复合值)——
              // 那才是主控/闪存里更热的点。旧实现只取"第一个匹配",永远拿到复合值,
              // 更热的那路被丢掉。现在按名字区分:复合值 → _0,最高的其余一路 → _1。
              // "已占位"= 已提交的 _extraRaw(跨 tick 首见定盘) 或 本 tick 的 pendingExtra。
              if (sensor.SensorType == LibreSensorType.Temperature) {
                int v = (int)sensor.Value.GetValueOrDefault();
                if (v >= 1 && v <= 120) {
                  string id = NvmeTempSensorId(sensor.Name);
                  // 同 ID 只取首次读到的;不同 ID 互不阻塞
                  if (!_extraRaw.ContainsKey(id) && !pendingExtra.ContainsKey(id)) {
                    pendingExtra[id] = v; _extraSeenThisTick[id] = true;
                  }
                }
              }
            } else if (hardware.HardwareType == LibreHardwareType.Motherboard || hardware.HardwareType == LibreHardwareType.SuperIO) {
              // SuperIO/主板第一个温度(PCH 等已在 OMEN WMI 0x23 那路独立显示,本路补位非 OMEN 机型)
              if (sensor.SensorType == LibreSensorType.Temperature && !_extraRaw.ContainsKey("MOTHERBOARD_SUPERIO")
                  && !pendingExtra.ContainsKey("MOTHERBOARD_SUPERIO")) {
                int v = (int)sensor.Value.GetValueOrDefault();
                if (v >= 1 && v <= 120) { pendingExtra["MOTHERBOARD_SUPERIO"] = v; _extraSeenThisTick["MOTHERBOARD_SUPERIO"] = true; }
              }
            }
          }
        }
      }

      // ponytail: 循环尾一次性提交本 tick 的 raw。锁粒度=字典操作本身, 上面 hardware.Update()
      // 的实 I/O(PawnIO/EC/NVAPI/WMI)全程在锁外, UI 读侧不会因硬件轮询卡住。
      lock (_extraLock) {
        foreach (var kv in pendingExtra) _extraRaw[kv.Key] = kv.Value;
      }

      CPUClock = snapCpuClock;
      GPUClock = snapGpuClock;

      float tempCPU = 50;
      // ponytail: reject physically impossible temps (<15°C or >120°C) to prevent
      // sensor glitches from polluting EMA and triggering wrong fan behavior
      if (libreTempCPU >= 15 && libreTempCPU <= 120)
        tempCPU = libreTempCPU;
      _rawCpuTemp = tempCPU;
      CPUTemp = tempCPU * RespondSpeed + CPUTemp * (1.0f - RespondSpeed);

      // ponytail: 额外传感器 EMA 平滑(同 CPU/GPU 口径,1~120°C 钳位)。只有本轮读到才更新,
      // 读不到保留上次平滑值,避免 LHM 间歇读到 null 让 UI 抖为 "-"。
      lock (_extraLock) {
        foreach (var id in ExtraSensorIds) {
          if (_extraSeenThisTick.TryGetValue(id, out bool seen) && seen &&
              _extraRaw.TryGetValue(id, out float raw) && raw >= 1 && raw <= 120) {
            float prev = _extraSmoothed.TryGetValue(id, out float p) ? p : raw;
            _extraSmoothed[id] = raw * RespondSpeed + prev * (1.0f - RespondSpeed);
          }
        }
      }

      if (librePowerCPU >= 0)
        CPUPower = librePowerCPU;

      afterLibre:
      if (ConfigService.HWiNFOReadEnabled)
        GpuTargetAvailable = GpuTempFresh || GpuPowerFresh;
      // ponytail: IR(红外)温度 — 经 OMEN WMI 0x23 通道读(sensorIndex 0),1~120°C 钳位 + EMA 平滑
      // (同 CPU/GPU 口径)。读不到(无 IR 传感器)保持负值,风扇 max 忽略。HWiNFO 路径也走这里,
      // 保证 IR 数据始终可供 UI 缓存与风扇曲线使用。
      {
        int ir = OmenHardware.GetSensorTemperature(0);
        if (ir >= 1 && ir <= 120) {
          _rawIrTemp = ir;
          float prev = IrTemp;
          IrTemp = (prev < 0) ? ir : ir * RespondSpeed + prev * (1.0f - RespondSpeed);
        }
      }
      // Auto GPU monitoring logic
      if (countQuery <= 5 && MonitorGPU)
        countQuery++;

      // Auto-disable GPU monitoring (ponytail: 只在电池供电时自动关)
      bool freshGpuPower = GpuPowerFresh;
      if (countQuery > 5 && AutoStopMonitorGPU && !PowerOnline && !IsConnectedToNVIDIA
          && MonitorGPU && freshGpuPower && GPUPower <= 1.3f) {
        GPUPower = 0;
        countQuery = 0;
        MonitorGPU = false;
        AutoStartMonitorGPU = true;
        LibreComputer.IsGpuEnabled = false;
        // 不 Save：只同步本次运行的 UI 状态，注册表中的用户偏好保持不变。
        ConfigService.MonitorGPU = false;
        OnGpuMonitoringChanged?.Invoke(false, "检测到显卡进入低功耗状态，OXH已停止监控GPU以节约能源。\n手动打开GPU监控后，本次将不再自动停止监控GPU。");
      }

      // Auto-enable GPU monitoring
      if (AutoStartMonitorGPU && IsConnectedToNVIDIA && !MonitorGPU) {
        GPUPower = 0;
        countQuery = 0;
        MonitorGPU = true;
        AutoStopMonitorGPU = true;
        LibreComputer.IsGpuEnabled = true;
        // 不 Save：只恢复本次运行的 UI 状态，注册表中的用户偏好保持不变。
        ConfigService.MonitorGPU = true;
        OnGpuMonitoringChanged?.Invoke(true, "检测到显卡连接到显示器，OXH已开始监控GPU。\n手动关闭GPU监控后，本次将不再自动开始监控GPU。");
      }

      if (!MonitorGPU && LibreComputer.IsGpuEnabled) {
        LibreComputer.IsGpuEnabled = false;
      }
    }

    public static void SetMonitorGPU(bool enabled) {
      if (enabled) {
        MonitorGPU = true;
        AutoStartMonitorGPU = true;
        AutoStopMonitorGPU = false;  // ponytail: manual on overrides auto-stop
        LibreComputer.IsGpuEnabled = true;
      } else {
        MonitorGPU = false;
        AutoStartMonitorGPU = false;  // ponytail: manual off overrides auto-start
        AutoStopMonitorGPU = true;
        LibreComputer.IsGpuEnabled = false;
      }
    }

    public static void RestoreMonitorGPU(bool enabled) {
      MonitorGPU = enabled;
      LibreComputer.IsGpuEnabled = enabled;
      AutoStartMonitorGPU = enabled;
      AutoStopMonitorGPU = enabled;
      if (!enabled) InvalidateGpuSamples();
    }

    // ═══════════════════════════════════════════════════════
    // Monitor Text Generation
    // ═══════════════════════════════════════════════════════
    public static string GetMonitorText() {
      var sb = new System.Text.StringBuilder();
      if (CPUPower > 0.01f)
        sb.AppendFormat("CPU: {0:F1}°C, {1:F1}W", CPUTemp, CPUPower);
      else {
        // ponytail: PawnIOState 形如 "v1.3.0 (RUNNING)"(GetPawnIOState 拼版本号),
        // 精确 == "RUNNING" 恒假 → 永远走不到"准备中"分支。按子串判断恢复原意。
        if (PawnIOState.Contains("RUNNING"))
          sb.Append("CPU: ").Append(Strings.MonitorPrepareLabel);
        else if (!string.IsNullOrEmpty(PawnIOState))
          sb.Append("CPU: PawnIO ").Append(PawnIOState);
      }
      if (MonitorGPU) {
        if (sb.Length > 0) sb.Append('\n');
        if (!TryGetFreshGpuTemp(out float gpuTemp))
          sb.Append("GPU: ").Append(Strings.MonitorPrepareLabel);
        else
          sb.AppendFormat("GPU: {0:F1}°C, {1:F1}W", gpuTemp, GpuPowerUsable ? GPUPower : 0);
      }
      if (MonitorFan) {
        if (sb.Length > 0) sb.Append('\n');
        sb.Append("Fan:  ").Append(GetDisplayFanSpeedPct(FanSpeedNow[0])).Append(", ").Append(GetDisplayFanSpeedPct(FanSpeedNow[1]));
      }
      if (sb.Length == 0) sb.Append(Strings.MonitorClosed);
      return sb.ToString();
    }

    public static void ApplyDisplayMode() {
      // ponytail: DisplayMode controls display only (raw vs smoothed).
      // RespondSpeed is managed by TempSensitivity. Keep them separate.
      _displayRaw = ConfigService.DisplayMode == "raw";
    }
    static bool _displayRaw = false;

    /// <summary>Return display temperature: raw or EMA-smoothed based on DisplayMode.</summary>
    public static float GetDisplayCpuTemp() => _displayRaw ? _rawCpuTemp : CPUTemp;
    public static float GetDisplayGpuTemp() => TryGetFreshGpuTemp(out float value)
      ? (_displayRaw ? _rawGpuTemp : value) : 0;
    // ponytail: IR 温度无 raw 快照(直接 WMI 读),统一返回 EMA 平滑值;负值=未读到。
    public static float GetDisplayIrTemp() => IrTemp;

    // ponytail: 风扇档位显示钳位 —— _fanSpeedNow 的 -1 是"首拍必写"哨兵(见:52), 启动首拍 EC 还没
    // 读到时直出就是 "-100"。只钳显示层,写侧哨兵语义不动(风扇侧靠 -1 保证第一拍一定刷新)。
    // 同 HWiNFOService.cs:62 外发前的 Math.Max(0,·) 先例。
    public static int GetDisplayFanSpeedPct(int level) => Math.Max(0, level) * 100;

    // ponytail: fan 计算路径专用 raw 温度 —— 自定义曲线/smart 模式走 GetSmartFanSpeed 时
    // 用原始读数(不叠加 RespondSpeed 温度层 EMA,避免双重平滑响应过肉);预设档仍走 CPUTemp/
    // GPUTemp 平滑值。GPU raw 访问时做范围校验,防止传感器毛刺直接进查表(写侧 GPUTemp 已 clamp,
    // 这里对未 clamp 的 raw 补一刀)。
    public static float RawCpuTemp => _rawCpuTemp;
    public static float RawGpuTemp => TryGetFreshGpuTemp(out _)
      && _rawGpuTemp >= 15 && _rawGpuTemp <= 120 ? _rawGpuTemp : RawCpuTemp;
    public static float RawIrTemp => _rawIrTemp;

    // ponytail: CPU 功耗显示上限 300W —— 传感器偶发读到荒谬值(如 5000W)会误导用户。
    // 仅约束显示,不影响 CPUPower 内部逻辑(风扇/GPU 自动启停仍用原始值)。
    const float CpuPowerDisplayMaxW = 300f;
    public static float GetDisplayCpuPower() => CPUPower >= 0 ? Math.Min(CPUPower, CpuPowerDisplayMaxW) : 0f;

    // ponytail: 功耗 EMA 每个采样只推进一次；getter 必须保持纯读取，避免 UI 调用次数改变平滑速度。
    static float _gpuPowerDisplay = 0;
    const float GpuPowerDisplayMaxW = 400f;
    public static float GetDisplayGpuPower() => GpuPowerUsable
      ? Math.Min(Math.Max(_gpuPowerDisplay, 0), GpuPowerDisplayMaxW) : 0;
    static float _rawCpuTemp, _rawGpuTemp, _rawIrTemp = -1;

    // ponytail: 最小可运行检查——非法样本不能刷新状态，显示 getter 不能偷偷推进 EMA。
    /// <summary>NVMe 温度传感器名 → extra sensor ID。复合值(Temperature / Temperature 1)
    /// 归 _0,其余(实测 Temperature 2)归 _1。纯函数,便于自检。</summary>
    /// <remarks>
    /// ponytail: NVMe 通常报 3 路:Temperature / Temperature 1 完全相等(复合值),
    /// Temperature 2 是主控/闪存更热点(实测 48~58℃ 随负载动)。旧实现"取第一个匹配"
    /// 永远拿到复合值,更热那路被丢掉。按名字区分比"第几个"稳 —— 不同厂商的
    /// 传感器顺序不保证一致。
    /// </remarks>
    internal static string NvmeTempSensorId(string sensorName) {
      string nm = sensorName ?? "";
      return (nm == "Temperature" || nm == "Temperature 1") ? "STORAGE_NVME_0" : "STORAGE_NVME_1";
    }

    public static string SelfCheck() {
      float oldTemp = GPUTemp, oldPower = GPUPower, oldRaw = _rawGpuTemp, oldDisplay = _gpuPowerDisplay;
      DateTime oldTempUtc = _lastGpuTempSampleUtc, oldPowerUtc = _lastGpuPowerSampleUtc;
      GpuTemperatureSource oldSource = _gpuTempSource;
      bool oldTarget = GpuTargetAvailable, oldMonitor = MonitorGPU;
      try {
        InvalidateGpuSamples();
        bool rejectsBad = !UpdateGpuTempSample(float.NaN) && !UpdateGpuPowerSample(float.PositiveInfinity)
          && !GpuTempFresh && !GpuPowerFresh;
        MonitorGPU = true;
        GpuTargetAvailable = true;
        bool acceptsGood = UpdateGpuTempSample(60) && UpdateGpuPowerSample(100);
        bool officialAccepted = UpdateGpuTempSample(65, GpuTemperatureSource.NvidiaNvApi);
        float officialValue = GPUTemp;
        bool sourcePriority = officialAccepted
          && !UpdateGpuTempSample(55, GpuTemperatureSource.LibreHardwareMonitor)
          && !UpdateGpuTempSample(54, GpuTemperatureSource.NvidiaNvml)
          && !UpdateGpuTempSample(53, GpuTemperatureSource.HWiNFO)
          && Math.Abs(GPUTemp - officialValue) < 0.001f;
        float first = GetDisplayGpuPower(), second = GetDisplayGpuPower();
        bool pureGetter = Math.Abs(first - second) < 0.001f;
        // -1 只该活在写侧哨兵里(见:52), 显示层必须钳 0 —— 否则启动首拍 UI 出 "-100"。
        bool fanSentinelClamped = GetDisplayFanSpeedPct(-1) == 0 && GetDisplayFanSpeedPct(0) == 0
          && GetDisplayFanSpeedPct(12) == 1200;
        // ponytail: NVMe 双路温度归类 —— 复合值进 _0,更热点进 _1。
        // 归类错了会把"更热的点"当成复合值显示,或反过来,用户看到的数字就少了意义。
        bool nvmeRouting = NvmeTempSensorId("Temperature") == "STORAGE_NVME_0"
          && NvmeTempSensorId("Temperature 1") == "STORAGE_NVME_0"
          && NvmeTempSensorId("Temperature 2") == "STORAGE_NVME_1"
          && NvmeTempSensorId("") == "STORAGE_NVME_1"
          && NvmeTempSensorId(null) == "STORAGE_NVME_1";
        // 两个 ID 都必须在清单里,否则设置页勾选项 / Dashboard 行会对不上
        bool nvmeIdsListed = ExtraSensorIds.Contains("STORAGE_NVME_0")
          && ExtraSensorIds.Contains("STORAGE_NVME_1");
        return rejectsBad && acceptsGood && sourcePriority && pureGetter && fanSentinelClamped
          && nvmeRouting && nvmeIdsListed
          ? "PASS GPU monitor sampling/source priority + fan sentinel clamp"
          : "FAIL GPU monitor sampling/source priority + fan sentinel clamp";
      } finally {
        GPUTemp = oldTemp; GPUPower = oldPower; _rawGpuTemp = oldRaw; _gpuPowerDisplay = oldDisplay;
        _lastGpuTempSampleUtc = oldTempUtc; _lastGpuPowerSampleUtc = oldPowerUtc; _gpuTempSource = oldSource;
        GpuTargetAvailable = oldTarget; MonitorGPU = oldMonitor;
      }
    }

    public static void Close() {
      LibreComputer.Close();
    }
  }
}
