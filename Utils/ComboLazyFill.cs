// ComboLazyFill.cs - 大列表 ComboBox 的惰性填充
// ponytail: PerfPage 的功率/电流/超频下拉各含上百项(10..254 W、160..255 A、-270..270 MHz…),
// 一次进页同步构建 600+ 个 ComboBoxItem ≈ 60ms,而用户每次只会选其中一个。改为:
//   - 进页时只放「当前选中值」这一项(以及 NotSet/关闭等固定项), 保证状态回填正确、界面不空;
//   - 用户首次展开下拉时, 再把完整列表填进去。
// 关键约束: 回填状态的代码靠遍历 Items 匹配文本/Tag, 所以「尚未展开」时必须能只凭一个值就选中 ——
// 用 SelectTagOrText 直接构造/定位该项, 不依赖完整列表。展开前 Items.Count 很小, 遍历也快。
using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace OmenSuperHub.Utils {
  /// <summary>一个惰性 ComboBox 的完整选项定义。</summary>
  internal sealed class LazyComboSpec {
    /// <summary>固定前缀项(NotSet / 关闭 / 恢复等), 始终存在。</summary>
    public List<ComboBoxItem> Fixed = new List<ComboBoxItem>();
    /// <summary>生成完整选项(含 Fixed 之外的区间项)。仅在首次展开时调用一次。</summary>
    public Func<IEnumerable<ComboBoxItem>> Build;
    /// <summary>完整列表是否已填充。</summary>
    public bool Filled;
    /// <summary>展开前用来占位的当前项(由调用方按配置值给出)。</summary>
    public ComboBoxItem Placeholder;
  }

  internal static class ComboLazyFill {
    static readonly Dictionary<ComboBox, LazyComboSpec> _specs
      = new Dictionary<ComboBox, LazyComboSpec>();

    /// <summary>登记一个惰性 ComboBox: 现在只填 Fixed + 占位项, 首次展开时填全部。</summary>
    public static void Register(ComboBox combo, LazyComboSpec spec) {
      if (combo == null || spec == null) return;
      _specs[combo] = spec;
      combo.Items.Clear();
      foreach (var it in spec.Fixed) combo.Items.Add(it);
      if (spec.Placeholder != null) {
        combo.Items.Add(spec.Placeholder);
        combo.SelectedItem = spec.Placeholder;
      }
      combo.DropDownOpened -= OnDropDownOpened;
      combo.DropDownOpened += OnDropDownOpened;
      // 页面被缓存/销毁后重新登记时避免重复订阅 —— 上面先减后加已保证幂等。
    }

    static void OnDropDownOpened(object sender, EventArgs e) {
      var combo = sender as ComboBox;
      if (combo == null) return;
      if (!_specs.TryGetValue(combo, out var spec) || spec.Filled) return;
      spec.Filled = true;

      // 记住当前选中项的 Tag, 填完整列表后按它重新定位(占位项会被替换掉)
      object keepTag = null;
      string keepText = null;
      if (combo.SelectedItem is ComboBoxItem sel) { keepTag = sel.Tag; keepText = sel.Content as string; }

      combo.Items.Clear();
      // ponytail: Fixed 必须一起补上 —— Build() 只负责区间项(10..254 W 之类), 像 "max"/"null"/NotSet
      // 这类固定项不在区间里; 漏了它们, 清空后按 Tag 重定位会 MISMATCH、选中值被清空。
      foreach (var it in spec.Fixed) combo.Items.Add(it);
      foreach (var it in spec.Build()) combo.Items.Add(it);

      if (keepTag != null || keepText != null) {
        foreach (ComboBoxItem it in combo.Items) {
          if (keepTag != null && Equals(it.Tag, keepTag)) { combo.SelectedItem = it; return; }
          if (keepTag == null && string.Equals(it.Content as string, keepText, StringComparison.Ordinal)) { combo.SelectedItem = it; return; }
        }
        // 占位项本身的值不在完整列表里(配置越界等): 原样保留占位项, 别让 UI 空着
        if (keepText != null) {
          var ph = new ComboBoxItem { Content = keepText, Tag = keepTag };
          combo.Items.Add(ph);
          combo.SelectedItem = ph;
        }
      }
    }

    /// <summary>不依赖完整列表地选中: 已填充就遍历匹配, 未填充就直接构造占位项。
    /// 返回是否命中(构造占位项也算命中, 因为值本身合法)。</summary>
    public static bool SelectTagOrText(ComboBox combo, object tag, string text) {
      if (combo == null) return false;
      if (_specs.TryGetValue(combo, out var spec) && spec.Filled) {
        foreach (ComboBoxItem it in combo.Items) {
          if (tag != null && Equals(it.Tag, tag)) { combo.SelectedItem = it; return true; }
        }
        if (text != null) {
          foreach (ComboBoxItem it in combo.Items)
            if (string.Equals(it.Content as string, text, StringComparison.Ordinal)) { combo.SelectedItem = it; return true; }
        }
        return false;
      }
      // 尚未展开: 用占位项承载当前值, 不构建整个列表
      var ph = new ComboBoxItem { Content = text, Tag = tag };
      if (spec != null) spec.Placeholder = ph;
      // 先移除旧的占位项(Fixed 项保持不动), 再挂新的
      for (int i = combo.Items.Count - 1; i >= 0; i--) {
        if (combo.Items[i] is ComboBoxItem it && spec != null && !spec.Fixed.Contains(it))
          combo.Items.RemoveAt(i);
      }
      if (combo.Items.Count == 0 && spec != null) foreach (var it in spec.Fixed) combo.Items.Add(it);
      combo.Items.Add(ph);
      combo.SelectedItem = ph;
      return true;
    }
  }
}
