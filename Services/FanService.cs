using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static OmenSuperHub.OmenHardware;

namespace OmenSuperHub.Services {
  internal static class FanService {
    // ═══════════════════════════════════════════════════════
    // Temperature-Fan Speed Mappings
    // ═══════════════════════════════════════════════════════
    public static Dictionary<float, List<int>> CPUTempFanMap = new Dictionary<float, List<int>>();
    public static Dictionary<float, List<int>> GPUTempFanMap = new Dictionary<float, List<int>>();

    // ═══════════════════════════════════════════════════════
    // Load Fan Configuration from file
    // ═══════════════════════════════════════════════════════
    public static void LoadFanConfig(string filePath) {
      float silentCoef = 1;
      if (filePath == "silent.txt")
        silentCoef = 0.8f;
      string absoluteFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filePath);
      if (File.Exists(absoluteFilePath)) {
        lock (CPUTempFanMap) {
          CPUTempFanMap.Clear();
          GPUTempFanMap.Clear();
        }
        var lines = File.ReadAllLines(absoluteFilePath);

        for (int i = 1; i < lines.Length; i++) {
          var parts = lines[i].Split(',');
          if (parts.Length == 6) {
            if (float.TryParse(parts[0], out float cpuTemp) &&
                int.TryParse(parts[1], out int cpuFan1Speed) &&
                int.TryParse(parts[2], out int cpuFan2Speed) &&
                float.TryParse(parts[3], out float gpuTemp) &&
                int.TryParse(parts[4], out int gpuFan1Speed) &&
                int.TryParse(parts[5], out int gpuFan2Speed)) {
              lock (CPUTempFanMap) {
                CPUTempFanMap[cpuTemp] = new List<int> { cpuFan1Speed, cpuFan2Speed };
                GPUTempFanMap[gpuTemp] = new List<int> { gpuFan1Speed, gpuFan2Speed };
              }
            }
          } else {
            Console.WriteLine($"{absoluteFilePath} error.");
            LoadDefaultFanConfig(absoluteFilePath, silentCoef);
            return;
          }
        }
      } else {
        Console.WriteLine($"{absoluteFilePath} not found.");
        LoadDefaultFanConfig(absoluteFilePath, silentCoef);
      }
    }

    // ═══════════════════════════════════════════════════════
    // Load Default Fan Config from BIOS
    // ═══════════════════════════════════════════════════════
    public static void LoadDefaultFanConfig(string filePath, float silentCoef) {
      byte[] fanTableBytes = GetFanTable();

      int numberOfFans = fanTableBytes[0];
      if (numberOfFans != 2) {
        System.Windows.MessageBox.Show("本机型不受支持！", "提示",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        GenerateDefaultMapping(filePath);
        return;
      }
      int numberOfEntries = fanTableBytes[1];

      int originalMin = int.MaxValue;
      int originalMax = int.MinValue;

      for (int i = 0; i < numberOfEntries; i++) {
        int baseIndex = 2 + i * 3;
        int tempThreshold = fanTableBytes[baseIndex + 2];
        if (tempThreshold < originalMin) originalMin = tempThreshold;
        if (tempThreshold > originalMax) originalMax = tempThreshold;
      }

      float targetMin = 50.0f;
      float targetMax = 97.0f;

      lock (CPUTempFanMap) {
        CPUTempFanMap.Clear();
        GPUTempFanMap.Clear();

        for (int i = 0; i < numberOfEntries; i++) {
          int baseIndex = 2 + i * 3;
          int fan1Speed = fanTableBytes[baseIndex];
          int fan2Speed = fanTableBytes[baseIndex + 1];
          int originalTempThreshold = fanTableBytes[baseIndex + 2];

          float cpuTempThreshold = targetMin +
              (originalTempThreshold - originalMin) * (targetMax - targetMin) / (originalMax - originalMin);
          float gpuTempThreshold = cpuTempThreshold - 10.0f;

          if (originalTempThreshold == originalMin || originalTempThreshold == originalMax) {
            if (!CPUTempFanMap.ContainsKey(cpuTempThreshold)) {
              CPUTempFanMap[cpuTempThreshold] = new List<int>();
            }
            CPUTempFanMap[cpuTempThreshold].Add((int)(fan1Speed * silentCoef) * 100);
            CPUTempFanMap[cpuTempThreshold].Add((int)(fan2Speed * silentCoef) * 100);

            if (!GPUTempFanMap.ContainsKey(gpuTempThreshold)) {
              GPUTempFanMap[gpuTempThreshold] = new List<int>();
            }
            GPUTempFanMap[gpuTempThreshold].Add((int)(fan1Speed * silentCoef) * 100);
            GPUTempFanMap[gpuTempThreshold].Add((int)(fan2Speed * silentCoef) * 100);
          }
        }
      }

      var lines = new List<string> { "CPU,Fan1,Fan2,GPU,Fan1,Fan2" };
      lines.AddRange(CPUTempFanMap.Select(kvp =>
          $"{kvp.Key:F0},{kvp.Value[0]},{kvp.Value[1]},{kvp.Key - 10.0:F0},{kvp.Value[0]},{kvp.Value[1]}"));
      File.WriteAllLines(filePath, lines);
    }

    // ═══════════════════════════════════════════════════════
    // Generate Default Mapping
    // ═══════════════════════════════════════════════════════
    public static void GenerateDefaultMapping(string filePath) {
      lock (CPUTempFanMap) {
        CPUTempFanMap.Clear();
        CPUTempFanMap[30] = new List<int> { 0, 0 };
        CPUTempFanMap[50] = new List<int> { 1600, 1900 };
        CPUTempFanMap[60] = new List<int> { 2000, 2300 };
        CPUTempFanMap[85] = new List<int> { 4000, 4300 };
        CPUTempFanMap[100] = new List<int> { 6100, 6400 };

        GPUTempFanMap.Clear();
        foreach (var kvp in CPUTempFanMap) {
          GPUTempFanMap[kvp.Key - 10] = new List<int> { kvp.Value[0], kvp.Value[1] };
        }
      }
      var lines = new List<string> { "CPU,Fan1,Fan2,GPU,Fan1,Fan2" };
      lines.AddRange(CPUTempFanMap.Select(kvp =>
          $"{kvp.Key:F0},{kvp.Value[0]},{kvp.Value[1]},{kvp.Key - 10:F0},{kvp.Value[0]},{kvp.Value[1]}"));
      File.WriteAllLines(filePath, lines);
    }

    // ═══════════════════════════════════════════════════════
    // Fan Speed Calculation with Interpolation
    // ═══════════════════════════════════════════════════════
    public static int GetFanSpeedForTemperature(int fanIndex) {
      if (CPUTempFanMap.Count == 0 || GPUTempFanMap.Count == 0) return 0;

      int cpuFanSpeed = GetFanSpeedForSpecificTemperature(HardwareService.CPUTemp, CPUTempFanMap, fanIndex);

      if (HardwareService.MonitorGPU) {
        int gpuFanSpeed = GetFanSpeedForSpecificTemperature(HardwareService.GPUTemp, GPUTempFanMap, fanIndex);
        return Math.Max(cpuFanSpeed, gpuFanSpeed);
      }

      return cpuFanSpeed;
    }

    public static int GetFanSpeedForSpecificTemperature(float temperature, Dictionary<float, List<int>> tempFanMap, int fanIndex) {
      var lowerBound = tempFanMap.Keys
                      .OrderBy(k => k)
                      .Where(t => t <= temperature)
                      .DefaultIfEmpty(tempFanMap.Keys.Min())
                      .LastOrDefault();

      var upperBound = tempFanMap.Keys
                      .OrderBy(k => k)
                      .Where(t => t > temperature)
                      .DefaultIfEmpty(tempFanMap.Keys.Max())
                      .FirstOrDefault();

      if (lowerBound == upperBound) {
        return tempFanMap[lowerBound][fanIndex];
      }

      int lowerSpeed = tempFanMap[lowerBound][fanIndex];
      int upperSpeed = tempFanMap[upperBound][fanIndex];
      float lowerTemp = lowerBound;
      float upperTemp = upperBound;

      float interpolatedSpeed = lowerSpeed + (upperSpeed - lowerSpeed) * (temperature - lowerTemp) / (upperTemp - lowerTemp);
      return (int)interpolatedSpeed;
    }

    // ═══════════════════════════════════════════════════════
    // Custom Fan Curve Persistence
    // ═══════════════════════════════════════════════════════
    public static List<(float temp, int rpm)> LoadCustomCurve() {
      string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "custom.txt");
      var result = new List<(float, int)>();
      if (!File.Exists(filePath)) return result;

      var lines = File.ReadAllLines(filePath);
      for (int i = 1; i < lines.Length; i++) {
        var parts = lines[i].Split(',');
        if (parts.Length >= 2 &&
            float.TryParse(parts[0], out float temp) &&
            int.TryParse(parts[1], out int rpm)) {
          result.Add((temp, rpm));
        }
      }
      return result;
    }

    public static void SaveCustomCurve(List<(float temp, int rpm)> points) {
      string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "custom.txt");
      var sorted = points.OrderBy(p => p.temp).ToList();
      var lines = new List<string> { "Temp,RPM" };
      foreach (var pt in sorted) {
        lines.Add($"{pt.temp:F0},{pt.rpm}");
      }
      File.WriteAllLines(filePath, lines);
    }

    public static void ApplyCustomCurve(List<(float temp, int rpm)> points) {
      var sorted = points.OrderBy(p => p.temp).ToList();
      lock (CPUTempFanMap) {
        CPUTempFanMap.Clear();
        GPUTempFanMap.Clear();
        foreach (var pt in sorted) {
          CPUTempFanMap[pt.temp] = new List<int> { pt.rpm, pt.rpm };
          float gpuTemp = pt.temp - 10f;
          if (gpuTemp < 0) gpuTemp = 0;
          GPUTempFanMap[gpuTemp] = new List<int> { pt.rpm, pt.rpm };
        }
      }
    }
  }
}
