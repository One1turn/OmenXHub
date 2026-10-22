// ComboSync.cs - Tag 驱动的 ComboBox 增量同步
// ponytail: 三个页面(Dashboard / Fan / Perf)都用同一套「枚举预设 → Items.Clear() → 逐条 Add」
// 重建下拉框,而这些页面都被 CachedPageService 缓存、每次进入都重跑一遍 Loaded ——
// 用户第一眼看到的总是列表先塌掉再重新长出来。键序没变时只刷新文案/选中项即可。
// 依赖项只到 System.Windows.Controls,所以放在 Utils 而不是 PresetManager(服务层不引 WPF)。
using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace OmenSuperHub.Utils {
  internal static class ComboSync {
    /// <summary>现有条目与目标键序列是否一致(只比 Tag,不比显示名 —— 重命名走轻路径)。</summary>
    public static bool TagsMatch(ItemCollection items, IList<string> keys) {
      if (items == null || keys == null || items.Count != keys.Count) return false;
      for (int i = 0; i < keys.Count; i++) {
        if (!(items[i] is ComboBoxItem ci) || (ci.Tag as string) != keys[i]) return false;
      }
      return true;
    }

    /// <summary>
    /// 把 (display,key) 列表同步进 combo:键序一致时只更新变了的文案,否则整表重建。
    /// 选中项按 currentKey 定位;找不到时用 fallbackIndex(&lt;0 表示不选中)。
    /// 返回是否发生了重建 —— 调用方可据此决定要不要做额外的重置工作。
    /// </summary>
    public static bool SyncTagged(ComboBox combo, IList<(string Display, string Key)> all,
                                  string currentKey, int fallbackIndex = 0) {
      var keys = new List<string>(all.Count);
      foreach (var a in all) keys.Add(a.Key);

      bool rebuilt = !TagsMatch(combo.Items, keys);
      if (rebuilt) {
        combo.Items.Clear();
        foreach (var a in all) combo.Items.Add(new ComboBoxItem { Content = a.Display, Tag = a.Key });
      } else {
        for (int i = 0; i < all.Count; i++) {
          if (combo.Items[i] is ComboBoxItem ci &&
              !string.Equals(ci.Content as string, all[i].Display, StringComparison.Ordinal))
            ci.Content = all[i].Display;
        }
      }

      int idx = -1;
      for (int i = 0; i < all.Count; i++) {
        if (all[i].Key == currentKey) { idx = i; break; }
      }
      if (idx < 0) idx = fallbackIndex;
      if (idx >= 0 && idx < combo.Items.Count && combo.SelectedIndex != idx)
        combo.SelectedIndex = idx;

      return rebuilt;
    }

    /// <summary>按 Tag 选中;找不到返回 false 且不改动选中项。</summary>
    public static bool SelectByTag(ComboBox combo, string tag) {
      for (int i = 0; i < combo.Items.Count; i++) {
        if (combo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == tag) {
          combo.SelectedIndex = i;
          return true;
        }
      }
      return false;
    }

    /// <summary>当前选中项的 Tag;无选中返回 ""。</summary>
    public static string SelectedTag(ComboBox combo) =>
      (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>纯函数自检:TagsMatch 的等价判定(不建 ComboBox 的部分由调用方构造)。</summary>
    public static string SelfCheck() {
      var fails = new List<string>();
      void Check(bool ok, string what) { if (!ok) fails.Add(what); }

      var combo = new ComboBox();
      void Seed(params string[] keys) {
        combo.Items.Clear();
        foreach (var k in keys) combo.Items.Add(new ComboBoxItem { Content = "名-" + k, Tag = k });
      }

      Seed("Extreme", "GpuPriority", "LightUse");
      Check(TagsMatch(combo.Items, new[] { "Extreme", "GpuPriority", "LightUse" }), "同序同键应一致");
      Check(!TagsMatch(combo.Items, new[] { "Extreme", "GpuPriority" }), "少一项应不一致");
      Check(!TagsMatch(combo.Items, new[] { "Extreme", "GpuPriority", "LightUse", "c1" }), "多一项应不一致");
      Check(!TagsMatch(combo.Items, new[] { "GpuPriority", "Extreme", "LightUse" }), "换序应不一致");
      Check(!TagsMatch(combo.Items, new[] { "Extreme", "GpuPriority", "c1" }), "换键应不一致");

      combo.Items.Clear();
      combo.Items.Add(new ComboBoxItem { Content = "旧名", Tag = "Extreme" });
      Check(TagsMatch(combo.Items, new[] { "Extreme" }), "仅显示名不同不应触发重建");

      combo.Items.Clear();
      Check(!TagsMatch(combo.Items, new[] { "Extreme" }), "空集合应不一致");
      combo.Items.Add("裸字符串");
      try { Check(!TagsMatch(combo.Items, new[] { "Extreme" }), "非 ComboBoxItem 应不一致"); }
      catch (Exception ex) { fails.Add("非 ComboBoxItem 抛异常: " + ex.GetType().Name); }

      // SyncTagged 端到端:同键序不应重建,且只改变了的文案
      combo.Items.Clear();
      combo.Items.Add(new ComboBoxItem { Content = "旧A", Tag = "A" });
      combo.Items.Add(new ComboBoxItem { Content = "B", Tag = "B" });
      var before = combo.Items[0];
      bool rebuilt = SyncTagged(combo, new List<(string, string)> { ("新A", "A"), ("B", "B") }, "A");
      Check(!rebuilt, "同键序不应重建");
      Check(ReferenceEquals(before, combo.Items[0]), "同键序时条目对象应保持不变");
      Check((combo.Items[0] as ComboBoxItem).Content as string == "新A", "文案应更新为新A");
      Check(combo.SelectedIndex == 0, "应按 currentKey 选中 A");

      // 键序变化必须重建
      rebuilt = SyncTagged(combo, new List<(string, string)> { ("B", "B"), ("A", "A") }, "A");
      Check(rebuilt, "换序应重建");
      Check(combo.SelectedIndex == 1, "重建后 A 应在新位置 1");

      // fallback
      combo.Items.Clear();
      SyncTagged(combo, new List<(string, string)> { ("A", "A"), ("B", "B") }, "不存在", 1);
      Check(combo.SelectedIndex == 1, "找不到 currentKey 应回退到 fallbackIndex");

      Check(SelectedTag(combo) == "B", "SelectedTag 应返回 B");
      Check(SelectByTag(combo, "A") && SelectedTag(combo) == "A", "SelectByTag 应能选中 A");
      Check(!SelectByTag(combo, "zzz") && SelectedTag(combo) == "A", "SelectByTag 未命中不应改动选中");

      return fails.Count == 0 ? "[ComboSync] PASS" : "[ComboSync] FAIL: " + string.Join("; ", fails.ToArray());
    }
  }
}
