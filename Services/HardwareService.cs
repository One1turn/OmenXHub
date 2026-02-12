using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LibreComputer = LibreHardwareMonitor.Hardware.Computer;
using LibreIHardware = LibreHardwareMonitor.Hardware.IHardware;
using LibreHardwareType = LibreHardwareMonitor.Hardware.HardwareType;
using LibreISensor = LibreHardwareMonitor.Hardware.ISensor;
using LibreSensorType = LibreHardwareMonitor.Hardware.SensorType;

namespace OmenSuperHub.Services {
  internal static class HardwareService {
    // ═══════════════════════════════════════════════════════
    // Hardware State
    // ═══════════════════════════════════════════════════════
    public static float CPUTemp = 50;
    public static float GPUTemp = 40;
    public static float CPUPower = 0;
    public static float GPUPower = 0;
    public static float RespondSpeed = 0.4f;
    public static bool MonitorGPU = true;
    public static bool MonitorFan = true;
    public static bool IsConnectedToNVIDIA = true;
    public static bool PowerOnline = true;
    public static List<int> FanSpeedNow = new List<int> { 20, 23 };

    // Internal state
    public static LibreComputer LibreComputer = new LibreComputer() { IsCpuEnabled = true, IsGpuEnabled = true };
    static bool openLib = true;
    static int countQuery = 0;
    public static bool AutoStartMonitorGPU = true, AutoStopMonitorGPU = true;
    static bool hasStartAuto = false, hasStopAuto = false;

    // ═══════════════════════════════════════════════════════
    // Display device detection (for GPU connection check)
    // ═══════════════════════════════════════════════════════
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct DISPLAY_DEVICE {
      [MarshalAs(UnmanagedType.U4)]
      public int cb;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
      public string DeviceName;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
      public string DeviceString;
      [MarshalAs(UnmanagedType.U4)]
      public DisplayDeviceStateFlags StateFlags;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
      public string DeviceID;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
      public string DeviceKey;
    }

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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool EnumDisplayDevices(
        string lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice,
        uint dwFlags);

    public static void MonitorQuery() {
      if (Screen.AllScreens.Length != 1)
        return;
      DISPLAY_DEVICE d = new DISPLAY_DEVICE();
      d.cb = Marshal.SizeOf(d);
      uint deviceNum = 0;

      while (EnumDisplayDevices(null, deviceNum, ref d, 0)) {
        if (d.StateFlags.HasFlag(DisplayDeviceStateFlags.AttachedToDesktop)) {
          if (d.DeviceString.Contains("Intel") || d.DeviceString.Contains("AMD")) {
            IsConnectedToNVIDIA = false;
            return;
          }
        }
        deviceNum++;
      }

      IsConnectedToNVIDIA = true;
    }

    // ═══════════════════════════════════════════════════════
    // Hardware Query
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Event raised when GPU monitoring state changes automatically.
    /// Args: (bool gpuEnabled, string message)
    /// </summary>
    public static event Action<bool, string> OnGpuMonitoringChanged;

    public static void QueryHardware() {
      float libreTempCPU = -300;
      float librePowerCPU = -1;
      bool getGPU = false;

      foreach (LibreIHardware hardware in LibreComputer.Hardware) {
        if (hardware.HardwareType == LibreHardwareType.Cpu || hardware.HardwareType == LibreHardwareType.GpuNvidia || hardware.HardwareType == LibreHardwareType.GpuAmd) {
          hardware.Update();

          foreach (LibreISensor sensor in hardware.Sensors) {
            if (hardware.HardwareType == LibreHardwareType.Cpu) {
              if (sensor.Name == "CPU Package" && sensor.SensorType == LibreSensorType.Temperature) {
                libreTempCPU = (int)sensor.Value.GetValueOrDefault();
              }
              if (sensor.Name == "CPU Package" && sensor.SensorType == LibreSensorType.Power) {
                librePowerCPU = sensor.Value.GetValueOrDefault();
              }
            } else if (MonitorGPU && hardware.HardwareType == LibreHardwareType.GpuNvidia) {
              if (sensor.Name == "GPU Core" && sensor.SensorType == LibreSensorType.Temperature) {
                GPUTemp = (int)sensor.Value.GetValueOrDefault() * RespondSpeed + GPUTemp * (1.0f - RespondSpeed);
              }
              if (sensor.Name == "GPU Package" && sensor.SensorType == LibreSensorType.Power) {
                getGPU = true;
                if ((int)(sensor.Value.GetValueOrDefault() * 10) == 5900)
                  GPUPower = 0;
                else
                  GPUPower = sensor.Value.GetValueOrDefault();
              }
            }
          }
        }
      }

      if (openLib && libreTempCPU > -299 && librePowerCPU >= 0) {
        openLib = false;
      }

      float tempCPU = 50;
      if (libreTempCPU > -299)
        tempCPU = libreTempCPU;
      CPUTemp = tempCPU * RespondSpeed + CPUTemp * (1.0f - RespondSpeed);

      if (librePowerCPU >= 0)
        CPUPower = librePowerCPU;

      // Auto GPU monitoring logic
      if (countQuery <= 5 && MonitorGPU)
        countQuery++;

      // Auto-disable GPU monitoring
      if (countQuery > 5 && AutoStopMonitorGPU && !IsConnectedToNVIDIA && MonitorGPU && ((GPUPower >= 0 && GPUPower <= 1.3) || !getGPU)) {
        GPUPower = 0;
        hasStopAuto = true;
        countQuery = 0;
        MonitorGPU = false;
        hasStartAuto = false;
        AutoStartMonitorGPU = true;
        LibreComputer.IsGpuEnabled = false;
        ConfigService.MonitorGPU = false;
        ConfigService.Save("MonitorGPU");
        OnGpuMonitoringChanged?.Invoke(false, "检测到显卡进入低功耗状态，OXH已停止监控GPU以节约能源。\n手动打开GPU监控后，本次将不再自动停止监控GPU。");
      }

      // Auto-enable GPU monitoring
      if (AutoStartMonitorGPU && IsConnectedToNVIDIA && !MonitorGPU) {
        GPUPower = 0;
        hasStartAuto = true;
        countQuery = 0;
        MonitorGPU = true;
        hasStopAuto = false;
        AutoStopMonitorGPU = true;
        LibreComputer.IsGpuEnabled = true;
        ConfigService.MonitorGPU = true;
        ConfigService.Save("MonitorGPU");
        OnGpuMonitoringChanged?.Invoke(true, "检测到显卡连接到显示器，OXH已开始监控GPU。\n手动关闭GPU监控后，本次将不再自动开始监控GPU。");
      }

      if (!MonitorGPU && LibreComputer.IsGpuEnabled) {
        LibreComputer.IsGpuEnabled = false;
      }
    }

    public static void SetMonitorGPU(bool enabled) {
      if (enabled) {
        MonitorGPU = true;
        if (hasStopAuto)
          AutoStopMonitorGPU = false;
        hasStartAuto = false;
        AutoStartMonitorGPU = true;
        LibreComputer.IsGpuEnabled = true;
      } else {
        MonitorGPU = false;
        if (hasStartAuto)
          AutoStartMonitorGPU = false;
        hasStopAuto = false;
        AutoStopMonitorGPU = true;
        LibreComputer.IsGpuEnabled = false;
      }
    }

    // ═══════════════════════════════════════════════════════
    // Monitor Text Generation
    // ═══════════════════════════════════════════════════════
    public static string GetMonitorText() {
      string str = $"CPU: {CPUTemp:F1}°C, {CPUPower:F1}W";
      if (MonitorGPU)
        str += $"\nGPU: {GPUTemp:F1}°C, {GPUPower:F1}W";
      if (MonitorFan)
        str += $"\nFan:  {FanSpeedNow[0] * 100}, {FanSpeedNow[1] * 100}";
      return str;
    }

    public static void Close() {
      LibreComputer.Close();
    }
  }
}
