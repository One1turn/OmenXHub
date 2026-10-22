using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace OmenSuperHub.Services.NetworkBoost {
  /// <summary>进程级分流规则：进程名 + 出口通道（aggregation / direct / nic_ethernet / nic_wifi）+ 限速。</summary>
  public class RoutingRule {
    public string ProcessName { get; set; } = "";
    public string Outbound { get; set; } = "aggregation";
    public int LimitKBps { get; set; } = 0;  // 0 = 不限速
  }

  /// <summary>
  /// 规则持久化到本地 JSON 文件（%AppData%\OmenXHub\routing-rules.json），关闭程序后保留。
  /// 首次加载时若文件不存在则回退读取注册表旧数据（兼容迁移）。
  /// </summary>
  internal static class RoutingRuleStore {
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    // 审查修复 #26: 「反序列化抛/返 null」旧代码与「文件缺失/空」同路静默返空,
    // 页面任一编辑随后覆写 = 规则被清空。损坏 → 置 _corrupt + 拷 .bak + 记日志,
    // 由页面 SaveAll 拒绝覆写;文件缺失/空 = 正常空态,不拦。
    static bool _corrupt;
    public static bool Corrupt => _corrupt;

    public static string FilePath {
      get {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmenXHub");
        return Path.Combine(dir, "routing-rules.json");
      }
    }

    public static List<RoutingRule> Load() {
      string s = "";
      try {
        if (File.Exists(FilePath)) s = File.ReadAllText(FilePath);
      } catch (Exception ex) {
        // 读取异常(占用/权限)是暂时性故障,不是内容损坏 — 按空态返回,不拦保存
        Logger.Warn("[RoutingRuleStore] 读取失败(按空处理): " + ex.Message);
        return new List<RoutingRule>();
      }
      if (string.IsNullOrEmpty(s)) s = ConfigService.BoostRulesJson; // 兼容旧注册表数据
      if (string.IsNullOrEmpty(s)) { _corrupt = false; return new List<RoutingRule>(); }
      try {
        var list = Json.Deserialize<List<RoutingRule>>(s);
        if (list == null) throw new InvalidDataException("deserialize returned null");
        _corrupt = false;
        return list;
      } catch (Exception ex) {
        _corrupt = true;
        try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true); } catch { }
        Logger.Warn("[RoutingRuleStore] 规则文件损坏,已备份 .bak 并暂停覆写: " + ex.Message);
        return new List<RoutingRule>();
      }
    }

    // 审查修复 #50: 旧空 catch 静默吞磁盘错误 → 编辑丢失无人知。改为上报 bool,页面提示。
    public static bool Save(List<RoutingRule> rules) {
      string json = Json.Serialize(rules ?? new List<RoutingRule>());
      try {
        string path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, json);
        _corrupt = false; // 成功覆写即解除损坏态
        return true;
      } catch (Exception ex) {
        Logger.Warn("[RoutingRuleStore] 保存失败(编辑未落盘): " + ex.Message);
        return false;
      }
    }
  }
}
