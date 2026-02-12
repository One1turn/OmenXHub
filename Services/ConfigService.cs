using System;
using Microsoft.Win32;

namespace OmenSuperHub.Services {
  internal static class ConfigService {
    private const string RegistryPath = @"Software\OmenXHub";

    // ═══════════════════════════════════════════════════════
    // Configuration State
    // ═══════════════════════════════════════════════════════
    public static string FanTable = "silent";
    public static string FanMode = "performance";
    public static string FanControl = "auto";
    public static string TempSensitivity = "high";
    public static string CpuPower = "max";
    public static string GpuPower = "max";
    public static int GpuClock = 0;
    public static int DBVersion = 2;
    public static string AutoStart = "off";
    public static int AlreadyRead = 0;
    public static string CustomIcon = "original";
    public static string OmenKey = "default";
    public static bool MonitorGPU = true;
    public static bool MonitorFan = true;
    public static int TextSize = 48;
    public static string FloatingBarLoc = "left";
    public static string FloatingBar = "off";

    // ═══════════════════════════════════════════════════════
    // Save Configuration
    // ═══════════════════════════════════════════════════════
    public static void Save(string configName = null) {
      try {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath)) {
          if (key == null) return;

          if (configName == null) {
            key.SetValue("FanTable", FanTable);
            key.SetValue("FanMode", FanMode);
            key.SetValue("FanControl", FanControl);
            key.SetValue("TempSensitivity", TempSensitivity);
            key.SetValue("CpuPower", CpuPower);
            key.SetValue("GpuPower", GpuPower);
            key.SetValue("GpuClock", GpuClock);
            key.SetValue("DBVersion", DBVersion);
            key.SetValue("AutoStart", AutoStart);
            key.SetValue("AlreadyRead", AlreadyRead);
            key.SetValue("CustomIcon", CustomIcon);
            key.SetValue("OmenKey", OmenKey);
            key.SetValue("MonitorGPU", MonitorGPU);
            key.SetValue("MonitorFan", MonitorFan);
            key.SetValue("FloatingBarSize", TextSize);
            key.SetValue("FloatingBarLoc", FloatingBarLoc);
            key.SetValue("FloatingBar", FloatingBar);
          } else {
            switch (configName) {
              case "FanTable": key.SetValue("FanTable", FanTable); break;
              case "FanMode": key.SetValue("FanMode", FanMode); break;
              case "FanControl": key.SetValue("FanControl", FanControl); break;
              case "TempSensitivity": key.SetValue("TempSensitivity", TempSensitivity); break;
              case "CpuPower": key.SetValue("CpuPower", CpuPower); break;
              case "GpuPower": key.SetValue("GpuPower", GpuPower); break;
              case "GpuClock": key.SetValue("GpuClock", GpuClock); break;
              case "DBVersion": key.SetValue("DBVersion", DBVersion); break;
              case "AutoStart": key.SetValue("AutoStart", AutoStart); break;
              case "AlreadyRead": key.SetValue("AlreadyRead", AlreadyRead); break;
              case "CustomIcon": key.SetValue("CustomIcon", CustomIcon); break;
              case "OmenKey": key.SetValue("OmenKey", OmenKey); break;
              case "MonitorGPU": key.SetValue("MonitorGPU", MonitorGPU); break;
              case "MonitorFan": key.SetValue("MonitorFan", MonitorFan); break;
              case "FloatingBarSize": key.SetValue("FloatingBarSize", TextSize); break;
              case "FloatingBarLoc": key.SetValue("FloatingBarLoc", FloatingBarLoc); break;
              case "FloatingBar": key.SetValue("FloatingBar", FloatingBar); break;
            }
          }
        }
      } catch (Exception ex) {
        Console.WriteLine($"Error saving configuration: {ex.Message}");
      }
    }

    // ═══════════════════════════════════════════════════════
    // Load Configuration (reads values only, does not apply)
    // ═══════════════════════════════════════════════════════
    public static void Load() {
      try {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath)) {
          if (key == null) return;

          FanTable = (string)key.GetValue("FanTable", "silent");
          FanMode = (string)key.GetValue("FanMode", "performance");
          FanControl = (string)key.GetValue("FanControl", "auto");
          TempSensitivity = (string)key.GetValue("TempSensitivity", "high");
          CpuPower = (string)key.GetValue("CpuPower", "max");
          GpuPower = (string)key.GetValue("GpuPower", "max");
          GpuClock = (int)key.GetValue("GpuClock", 0);
          DBVersion = (int)key.GetValue("DBVersion", 2);
          AutoStart = (string)key.GetValue("AutoStart", "off");
          AlreadyRead = (int)key.GetValue("AlreadyRead", 0);
          CustomIcon = (string)key.GetValue("CustomIcon", "original");
          OmenKey = (string)key.GetValue("OmenKey", "default");
          MonitorGPU = Convert.ToBoolean(key.GetValue("MonitorGPU", true));
          MonitorFan = Convert.ToBoolean(key.GetValue("MonitorFan", true));
          TextSize = (int)key.GetValue("FloatingBarSize", 48);
          FloatingBarLoc = (string)key.GetValue("FloatingBarLoc", "left");
          FloatingBar = (string)key.GetValue("FloatingBar", "off");
        }
      } catch (Exception ex) {
        Console.WriteLine($"Error loading configuration: {ex.Message}");
      }
    }

    /// <summary>
    /// Read a single icon config value (used early in startup before full load).
    /// </summary>
    public static string ReadIconConfig() {
      try {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath)) {
          if (key != null) {
            return (string)key.GetValue("CustomIcon", "original");
          }
        }
      } catch { }
      return "original";
    }
  }
}
