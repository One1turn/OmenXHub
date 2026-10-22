// AutomationService.cs - 自动化数据模型与持久化
// Pipeline/Trigger/Step 数据契约定义，JSON 序列化存储，步骤类型注册表
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using OmenSuperHub.Services;

namespace OmenSuperHub.Services {
  [DataContract]
  public class AutomationStep {
    [DataMember] public string Type { get; set; }
    [DataMember] public string Value { get; set; }
    [DataMember] public int IntValue { get; set; }
    [DataMember] public int DelayMs { get; set; }
  }

  [DataContract]
  public class AutomationTrigger {
    [DataMember] public string Type { get; set; }
    [DataMember] public string Value { get; set; }
    [DataMember] public bool Enabled { get; set; }

    public AutomationTrigger() { Enabled = true; }
    public AutomationTrigger(string type, string value = "") {
      Type = type; Value = value ?? ""; Enabled = true;
    }
  }

  [DataContract]
  public class AutomationPipeline {
    [DataMember] public string Name { get; set; }
    [DataMember] public bool Enabled { get; set; }
    [DataMember] public string TriggerType { get; set; }
    [DataMember] public string TriggerValue { get; set; }
    [DataMember] public List<AutomationTrigger> Triggers { get; set; }
    [DataMember] public List<AutomationStep> Steps { get; set; }

    public AutomationPipeline() {
      Steps = new List<AutomationStep>();
      Triggers = new List<AutomationTrigger>();
      Enabled = true;
    }

    public void EnsureTriggers() {
      if (Triggers == null) Triggers = new List<AutomationTrigger>();
      if (Triggers.Count == 0 && !string.IsNullOrEmpty(TriggerType) && TriggerType != "QuickAction") {
        Triggers.Add(new AutomationTrigger(TriggerType, TriggerValue));
      }
    }

    public bool MatchesTrigger(string triggerType, string triggerValue) {
      EnsureTriggers();
      if (Triggers.Count == 0) return false;
      foreach (var t in Triggers) {
        if (!t.Enabled) continue;
        if (t.Type != triggerType) continue;
        if (!string.IsNullOrEmpty(t.Value) && !MatchTriggerValue(t.Type, t.Value, triggerValue)) continue;
        return true;
      }
      return false;
    }

    static bool MatchTriggerValue(string triggerType, string triggerDefValue, string firedValue) {
      switch (triggerType) {
        case "ProcessStart":
        case "ProcessStop":
          // ponytail: WMI ProcessName 全小写且总带 .exe；用户输入可能大小写不同/漏 .exe — 归一化后比较
          return string.Equals(AutomationService.NormalizeProcName(triggerDefValue), AutomationService.NormalizeProcName(firedValue), StringComparison.OrdinalIgnoreCase);
        case "BatteryAbove":
          if (int.TryParse(triggerDefValue, out int batThresh) && int.TryParse(firedValue, out int batCur))
            return batCur >= batThresh;
          return false;
        case "BatteryBelow":
          if (int.TryParse(triggerDefValue, out int batThresh2) && int.TryParse(firedValue, out int batCur2))
            return batCur2 <= batThresh2;
          return false;
        case "CpuTempAbove":
          if (int.TryParse(triggerDefValue, out int cpuThresh) && int.TryParse(firedValue, out int cpuCur))
            return cpuCur >= cpuThresh;
          return false;
        case "GpuTempAbove":
          if (int.TryParse(triggerDefValue, out int gpuThresh) && int.TryParse(firedValue, out int gpuCur))
            return gpuCur >= gpuThresh;
          return false;
        default:
          return triggerDefValue == firedValue;
      }
    }

    public bool IsQuickAction {
      get {
        EnsureTriggers();
        return Triggers.Count == 1 && Triggers[0].Type == "QuickAction";
      }
    }
  }

  internal static class AutomationService {
    private static string FilePath;
    private static readonly object FileLock = new object();
    // ponytail: ListLock 只收"列表级"并发 —— UI 线程 Add/Remove/序列化枚举 vs 1s/2s/5s/15s
    // 轮询线程 foreach Pipelines。旧版无锁,并发时 foreach 抛 InvalidOperationException,
    // 而轮询调用点全在 catch{} 里 → 异常被吞 = 那一拍触发检测整轮静默丢失。
    // 残面(不算闭环): 返回的快照仍装着共享的 pipeline 对象,其内部 Triggers/Steps 由编辑器
    // UI 线程实时 Add/RemoveAt(PipelineEditorWindow),轮询 foreach p.Triggers 的同类吞一拍
    // 异常仍在。收它需编辑器改副本+OK 提交协议,改造面大,本轮不做。
    private static readonly object ListLock = new object();
    private static DataContractJsonSerializer _serializer;
    // ponytail: 引用整体替换(原子),增删只在 ListLock 内 → 锁内快照拿到的永远是完整列表(旧或新)。
    public static List<AutomationPipeline> Pipelines { get; private set; }

    internal static string NormalizeProcName(string name) {
      name = (name ?? "").Trim();
      return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    public static void Initialize() {
      string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      string dir = Path.Combine(appData, "OmenXHub");
      if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
      FilePath = Path.Combine(dir, "automation.json");
      _serializer = new DataContractJsonSerializer(typeof(List<AutomationPipeline>),
          new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
      Load();
    }

    public static void Load() {
      lock (FileLock) {
        try {
          if (File.Exists(FilePath)) {
            using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read))
              Pipelines = (List<AutomationPipeline>)_serializer.ReadObject(fs);
          } else {
            Pipelines = new List<AutomationPipeline>();
          }
        } catch (Exception ex) {
          Logger.Error("AutomationService.Load failed: " + ex.Message);
          Pipelines = new List<AutomationPipeline>();
        }
      }
      TrayService.RebuildMenu();
    }

    public static void Save() {
      // ponytail: 序列化(= 枚举整个管道图)在 ListLock 内做,文件 IO 在 ListLock 外 ——
      // 磁盘慢/被占用时不阻塞轮询线程取快照。payload 为 null 表示序列化失败:
      // 与旧版同义(旧版 WriteObject 抛异常 → 后面的 WriteAllBytes 根本不执行),不覆写文件。
      byte[] payload = null;
      lock (ListLock) {
        if (Pipelines == null) {
          // 未 Initialize 就 Save: 宁可跳过,也不能把磁盘上的用户管道覆写成空列表
          Logger.Error("AutomationService.Save skipped: Pipelines 未初始化");
        } else {
          try {
            using (var ms = new MemoryStream()) {
              _serializer.WriteObject(ms, Pipelines);
              payload = ms.ToArray();
            }
          } catch (Exception ex) {
            Logger.Error("AutomationService.Save failed: " + ex.Message);
          }
        }
      }
      if (payload != null) {
        lock (FileLock) {
          try {
            // ponytail: temp + Replace 原子换入（同 MacroService）—— 裸 WriteAllBytes
            // 半途截断后 Load 降级空列表,下一次 Save 会把用户管道永久覆盖为空。
            string tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, payload);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
          } catch (Exception ex) {
            Logger.Error("AutomationService.Save failed: " + ex.Message);
          }
        }
      }
      // ponytail: 管道增删/启用/禁用都过 Save —— 同步按需启停温度/电池/计划定时器,无需各自插钩子。
      AutomationProcessor.ReevaluateTimers();
      // ponytail: 启用/禁用管道也须刷新热键 —— automation.json 里 Enabled 翻转后,Hotkey 触发管道
      // 应同步注册/注销全局热键。RefreshHotkeys 有 _running 守卫 + 内部重注册,幂等。
      AutomationProcessor.RefreshHotkeys();
    }

    public static void AddPipeline(AutomationPipeline pipeline) {
      lock (ListLock) {
        if (Pipelines == null) Pipelines = new List<AutomationPipeline>();
        Pipelines.Add(pipeline);
      }
      Save();
      AutomationProcessor.RefreshHotkeys();
    }

    public static void RemovePipeline(AutomationPipeline pipeline) {
      lock (ListLock) {
        if (Pipelines == null) Pipelines = new List<AutomationPipeline>();
        Pipelines.Remove(pipeline);
      }
      Save();
      AutomationProcessor.RefreshHotkeys();
    }

    public static void UpdatePipeline(AutomationPipeline pipeline) {
      Save();
      AutomationProcessor.RefreshHotkeys();
    }

    public static List<AutomationPipeline> GetEnabledPipelines() {
      // 在 ListLock 内过滤并返回新建的副本 —— 调用方(轮询线程)拿到的列表不再被 UI 线程增删。
      var result = new List<AutomationPipeline>();
      lock (ListLock) {
        if (Pipelines == null) return result;
        foreach (var p in Pipelines) {
          if (!p.Enabled) continue;
          p.EnsureTriggers();
          if (p.IsQuickAction) continue;
          result.Add(p);
        }
      }
      return result;
    }

    public static List<AutomationPipeline> GetQuickActions() {
      var result = new List<AutomationPipeline>();
      lock (ListLock) {
        if (Pipelines == null) return result;
        foreach (var p in Pipelines) {
          if (p.Enabled && p.IsQuickAction)
            result.Add(p);
        }
      }
      return result;
    }
  }

  internal static class AutomationStepTypes {
    public static readonly string[] All = {
      "SetPreset", "SetRefreshRate", "SetPowerPlan", "SetPowerMode",
      "SetMaxFrameRate", "SetCpuPower", "SetGpuPower",
      "SetFanMode",
      "SetTempSensitivity", "SetGPUHybridMode", "SetBrightness",
      "SetMicrophone", "SetWiFi", "SetBluetooth", "PlaySound",
      "RunProgram", "Delay", "Notification", "RunMacro", "CleanMemory"
    };

    public static string GetLabel(string type) {
      switch (type) {
        case "SetPreset": return Strings.AutomationStepSetPreset;
        case "SetRefreshRate": return Strings.AutomationStepSetRefreshRate;
        case "SetPowerPlan": return Strings.AutomationStepSetPowerPlan;
        case "SetPowerMode": return Strings.AutomationStepSetPowerMode;
        case "SetMaxFrameRate": return Strings.AutomationStepSetMaxFrameRate;
        case "SetCpuPower": return Strings.AutomationStepSetCpuPower;
        case "SetGpuPower": return Strings.AutomationStepSetGpuPower;
        case "SetFanMode": return Strings.AutomationStepSetFanMode;
        case "SetTempSensitivity": return Strings.AutomationStepSetTempSensitivity;
        case "SetGPUHybridMode": return Strings.AutomationStepSetGPUHybridMode;
        case "SetBrightness": return Strings.AutomationStepSetBrightness;
        case "SetMicrophone": return Strings.AutomationStepSetMicrophone;
        case "SetWiFi": return Strings.AutomationStepSetWiFi;
        case "SetBluetooth": return Strings.AutomationStepSetBluetooth;
        case "PlaySound": return Strings.AutomationStepPlaySound;
        case "RunProgram": return Strings.AutomationStepRunProgram;
        case "Delay": return Strings.AutomationStepDelay;
        case "Notification": return Strings.AutomationStepNotification;
        case "RunMacro": return Strings.AutomationStepRunMacro;
        case "CleanMemory": return Strings.AutomationStepCleanMemory;
        default: return type;
      }
    }

    public static string GetValueHint(string type) {
      switch (type) {
        case "SetRefreshRate": return Strings.AutomationRefreshRate;
        case "SetPowerPlan": return Strings.AutomationPowerPlanGuid;
        case "SetPowerMode": return Strings.AutomationPowerModeValue;
        case "SetPreset": return Strings.AutomationPreset;
        case "SetMaxFrameRate": return Strings.AutomationMaxFrameRate;
        case "SetCpuPower": return Strings.AutomationCpuPowerValue;
        case "SetGpuPower": return Strings.AutomationGpuPowerValue;
        case "SetFanMode": return Strings.AutomationFanModeValue;
        case "SetTempSensitivity": return Strings.AutomationTempSensitivityValue;
        case "SetGPUHybridMode": return Strings.AutomationGPUHybridModeValue;
        case "SetBrightness": return Strings.AutomationBrightnessValue;
        case "SetMicrophone": return Strings.AutomationMicrophoneValue;
        case "SetWiFi": return Strings.AutomationWiFiValue;
        case "SetBluetooth": return Strings.AutomationBluetoothValue;
        case "PlaySound": return Strings.AutomationPlaySoundValue;
        case "RunProgram": return Strings.AutomationProgramPath;
        case "Notification": return Strings.AutomationMessage;
        case "RunMacro": return "Macro Name";
        case "CleanMemory": return Strings.AutomationCleanMemoryHint;
        default: return Strings.AutomationStepValue;
      }
    }
  }
}
