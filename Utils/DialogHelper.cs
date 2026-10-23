// DialogHelper.cs - WPF-UI 风格弹窗（替代 System.Windows.MessageBox）
// 使用 FluentWindow + Mica 背景，与自动化/宏编辑器二级菜单一致
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Wpf.Ui.Controls;
using UiSymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using UiSymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace OmenSuperHub.Utils {
  internal class DialogResultWindow : FluentWindow {
    // ponytail: `Result` field was never read or written — removed to silence CS0649
    public DialogResultWindow() {
      WindowStartupLocation = WindowStartupLocation.CenterOwner;
      ShowInTaskbar = false;
      ResizeMode = ResizeMode.NoResize;
      SizeToContent = SizeToContent.WidthAndHeight;
      ExtendsContentIntoTitleBar = true;
      WindowBackdropType = WindowBackdropType.Mica;
      Background = Brushes.Transparent;
      MinWidth = 360;
      MaxWidth = 520;
    }
  }

  internal static class DialogHelper {
    /// <summary>代码里动态拼弹窗时用的标准标题栏(图标+标题+关闭键+拖拽区)。
    /// ponytail: 5 处内联 FluentWindow 各自手搓标题 Border —— 全都没有拖拽区和系统关闭键,
    /// 且 ExtendsContentIntoTitleBar 已抹掉原生栏,用户只能找别处关。收成一处。
    /// Header 必须 IsHitTestVisible=False,否则 TitleBar 判定鼠标压在内容上就不发 HTCAPTION。</summary>
    public static Wpf.Ui.Controls.TitleBar MakeTitleBar(string title, UiSymbolRegular icon) {
      var bar = new Wpf.Ui.Controls.TitleBar {
        Title = title, ShowMinimize = false, ShowMaximize = false, ShowClose = true, CanMaximize = false,
        // ponytail: 与 XAML 里的 OmenDialogTitleBar 同一修法 —— 模板的 TitleGrid 会吃掉一份
        // Padding(默认 16),把 Header 推到 x=32,比正文的 16px 边距多缩进一格。
        Padding = new Thickness(16, 4, 0, 4),
        Header = new StackPanel {
          Orientation = Orientation.Horizontal,
          IsHitTestVisible = false,
          Children = {
            new UiSymbolIcon { Symbol = icon, FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
                               Margin = new Thickness(0, 0, 8, 0),
                               Foreground = _Brush("AccentOmenBrush", "#FFFFFF") },
            new System.Windows.Controls.TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
                                                    VerticalAlignment = VerticalAlignment.Center }
          }
        }
      };
      return bar;
    }

    static Window _Owner() {
      if (Application.Current == null) return null;
      for (int i = Application.Current.Windows.Count - 1; i >= 0; i--) {
        var w = Application.Current.Windows[i];
        if (w.IsActive && w.Visibility == Visibility.Visible) return w;
      }
      return Application.Current.MainWindow;
    }

    static SolidColorBrush _Brush(string key, string fallback) {
      var b = Application.Current?.TryFindResource(key) as SolidColorBrush;
      // ponytail: 冻结兜底刷 — Freeze 去线程亲和,防跨线程布局异常
      if (b != null) return b;
      return _Solid(fallback);
    }

    static SolidColorBrush _Solid(string hex) {
      var fb = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
      fb.Freeze();
      return fb;
    }

    // ponytail: 图标色改取主题 token(AccentBlue/Yellow/RedBrush),原先写死的三个十六进制
    // 在亮色主题下亮度不够(#FFB900 压在浅底上尤其糊)。token 本身随主题切换。
    public static void Info(string message, string title = "提示") =>
      _Show(message, title, UiSymbolRegular.Info24, "AccentBlueBrush", "#60CDFF", false, false);
    public static void Warn(string message, string title = "警告") =>
      _Show(message, title, UiSymbolRegular.Warning24, "AccentYellowBrush", "#FFB900", false, false);
    public static void Error(string message, string title = "错误") =>
      _Show(message, title, UiSymbolRegular.DismissCircle24, "AccentRedBrush", "#FF6B6B", false, false);
    public static bool Confirm(string message, string title = "确认") =>
      _Show(message, title, UiSymbolRegular.QuestionCircle24, "AccentBlueBrush", "#60CDFF", true, false) == 1;
    public static bool OkCancel(string message, string title = "确认") =>
      _Show(message, title, UiSymbolRegular.QuestionCircle24, "AccentBlueBrush", "#60CDFF", true, false) == 1;
    /// <summary>两选一对话框。返回 true=点了 primaryLabel,false=点了 secondaryLabel(任意键关闭等同 secondary)。</summary>
    public static bool Choice(string message, string title, string primaryLabel, string secondaryLabel) =>
      _Show(message, title, UiSymbolRegular.QuestionCircle24, "AccentBlueBrush", "#60CDFF", false, false, primaryLabel, secondaryLabel) == 1;
    /// <summary>返回 0=取消, 1=是, 2=否</summary>
    public static int YesNoCancel(string message, string title = "确认") =>
      (int)_Show(message, title, UiSymbolRegular.QuestionCircle24, "AccentBlueBrush", "#60CDFF", true, true);

    // ponytail: primaryLabel/secondaryLabel 可空(null=回退原硬编码 label),以兼容现有 Info/Warn/Confirm 调用者。
    // Choice 用它传自定义两按钮文案;其它路径不动 label 行为。
    static int _Show(string message, string title, UiSymbolRegular icon, string iconBrushKey, string iconFallback,
                     bool hasCancel, bool yesNoCancel,
                     string primaryLabel = null, string secondaryLabel = null) {
      var owner = _Owner();
      if (owner == null) {
        if (yesNoCancel) {
          var r = System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.YesNoCancel);
          return r == System.Windows.MessageBoxResult.Yes ? 1 : r == System.Windows.MessageBoxResult.No ? 2 : 0;
        }
        var r2 = System.Windows.MessageBox.Show(message, title, hasCancel ? System.Windows.MessageBoxButton.OKCancel : System.Windows.MessageBoxButton.OK);
        return r2 == System.Windows.MessageBoxResult.OK ? 1 : 0;
      }

      var result = new int[1];
      var root = BuildDialogBody(message, title, icon, iconBrushKey, iconFallback,
                                 hasCancel, yesNoCancel, primaryLabel, secondaryLabel, result);

      // ── Window ──
      var w = new DialogResultWindow();
      w.Content = root;
      w.Owner = owner;
      w.Title = title;
      // ponytail: 关闭前断开 Owner — 模态弹窗(ShowDialog)关闭时 WPF 对 owner 的恢复/激活
      // 与托盘/其他 owned window 焦点链交错会把主窗口误最小化(issue: 关闭弹窗后主界面最小化)。
      WindowHelper.DetachOwnerOnClose(w);
      w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { result[0] = 0; w.Close(); } };
      // Wire buttons to close
      foreach (var child in FindVisualChildren<System.Windows.Controls.Button>(root))
        child.Click += (_, __) => w.Close();

      w.ShowDialog();
      return result[0];
    }

    /// <summary>把弹窗内容树单独拼出来(不建窗口、不 ShowDialog),供 --selftest 断言骨架与严重度配色。</summary>
    internal static StackPanel BuildDialogBody(string message, string title, UiSymbolRegular icon, string iconBrushKey,
                                               string iconFallback, bool hasCancel, bool yesNoCancel,
                                               string primaryLabel, string secondaryLabel, int[] result) {
      var bg = _Brush("CardBackgroundFillColorDefaultBrush", "#2C2C2C");
      var fg = _Brush("TextPrimaryBrush", "#E0E0E0");
      var root = new StackPanel();

      // ── Title bar ──
      // ponytail: 用真 ui:TitleBar 而不是自绘 Border —— 自绘版没有拖拽区(WM_NCHITTEST 归 HTCAPTION
      // 的逻辑在 TitleBar 内部)。Header 必须 IsHitTestVisible=False,否则鼠标压在内容上就不发 HTCAPTION。
      // 模态提示框不给关闭 X:必须点按钮或 Esc,避免"关掉了但不知道结果"。
      //
      // ponytail: FIX(dialog-title-offset) —— 这里**必须**给 Title 赋值,即使 Header 里已经有标题文本。
      // 实测(Wpf.Ui 3.1.1,真窗口+模板已应用):
      //   Title=null              → TitleGrid Collapsed,Padding 完全失效,Header.x = 0  (左移 16px)
      //   Title="错误" + Pad(16,0,16,0) → TitleGrid Visible,Header.x = 16+16 = 32        (右移 16px)
      //   Title="错误" + Pad(16,0,0,0)  → Header.x = 16                                  ✓
      // 原因是模板把 TitleGrid 的 Margin 绑到 Padding,Header 又排在 TitleGrid 之后;
      // Title/Icon 都为空时 TitleGrid 折叠,那份 Padding 就整块消失了。
      // Title 本身在 Header 非空时不渲染(Header 覆盖显示区),纯粹是"让 TitleGrid 活着"的开关。
      var titleBar = new Wpf.Ui.Controls.TitleBar {
        Title = title,
        ShowMinimize = false, ShowMaximize = false, ShowClose = false,
        Height = 44, Padding = new Thickness(16, 0, 0, 0),
        Header = new StackPanel {
          Orientation = Orientation.Horizontal,
          IsHitTestVisible = false,
          Children = {
            new UiSymbolIcon { Symbol = icon, FontSize = 16, Foreground = _Brush(iconBrushKey, iconFallback), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
            new System.Windows.Controls.TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = fg }
          }
        }
      };
      root.Children.Add(new Border {
        BorderBrush = _Brush("BorderSubtleBrush", "#404040"), BorderThickness = new Thickness(0, 0, 0, 1),
        Child = titleBar
      });

      // ── Content card ──
      // ponytail: Info/Warn/Error 原来只有图标颜色不同,标题、底色、边框完全一致 ——
      // 一眼扫过去三种弹窗长得一样。补一条贴左边缘的 3px 严重度色条(InfoBar 的做法),
      // 颜色复用图标那把刷子,所以永远和图标同色,不会出现两套语义。
      var contentCard = new Border {
        Background = bg,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(0),
        // ponytail: 底部原来是 0 —— 窗口 SizeToContent,卡片会直接贴住窗口下沿(截图实测)。
        Margin = new Thickness(12, 8, 12, 12)
      };
      var contentStack = new StackPanel { Margin = new Thickness(16, 16, 16, 12) };
      contentStack.Children.Add(new System.Windows.Controls.TextBlock {
        Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13,
        Foreground = fg, LineHeight = 20, Margin = new Thickness(0, 0, 0, 16)
      });

      // ── Buttons ──
      var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

      void AddBtn(string label, int returnVal, bool primary) {
        // ponytail: 原实现手搓 System.Windows.Controls.Button + 硬编码底色(还引用了一个不存在的
        // SystemAccentBrushPrimary,实际永远走 #0078D4 兜底蓝),与全局品牌色不一致。
        var b = new Wpf.Ui.Controls.Button {
          Content = label, MinWidth = 80, Height = 32, Margin = new Thickness(6, 0, 0, 0),
          FontSize = 13,
          Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary
        };
        b.Click += (_, __) => { result[0] = returnVal; };
        btnRow.Children.Add(b);
      }

      AddBtn(primaryLabel ?? "确定", 1, true);
      if (secondaryLabel != null) {
        AddBtn(secondaryLabel, 0, false);
      } else if (yesNoCancel) {
        btnRow.Children.Clear();
        AddBtn("是", 1, true);
        AddBtn("否", 2, false);
        AddBtn("取消", 0, false);
      } else if (hasCancel) {
        AddBtn("取消", 0, false);
      }

      contentStack.Children.Add(btnRow);

      // 色条与内容并排:条贴卡片左缘(含圆角内缩),内容维持原 16px 内边距。
      var cardGrid = new Grid();
      cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
      cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      cardGrid.Children.Add(new Border {
        Name = "SeverityBar",
        Background = _Brush(iconBrushKey, iconFallback),
        CornerRadius = new CornerRadius(8, 0, 0, 8),
        Margin = new Thickness(0, 1, 0, 1)
      });
      Grid.SetColumn(contentStack, 1);
      cardGrid.Children.Add(contentStack);
      contentCard.Child = cardGrid;
      root.Children.Add(contentCard);

      return root;
    }

    static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject {
      if (parent == null) yield break;
      int count = VisualTreeHelper.GetChildrenCount(parent);
      for (int i = 0; i < count; i++) {
        var child = VisualTreeHelper.GetChild(parent, i);
        if (child is T t) yield return t;
        foreach (var gc in FindVisualChildren<T>(child)) yield return gc;
      }
    }
  }
}
