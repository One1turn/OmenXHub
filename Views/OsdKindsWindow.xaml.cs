// OsdKindsWindow.xaml.cs - OSD 提示类型二级弹窗:逐类开关屏幕提示
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using OmenSuperHub.Services;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using ToggleSwitch = Wpf.Ui.Controls.ToggleSwitch;

namespace OmenSuperHub.Views {
  public partial class OsdKindsWindow : FluentWindow {
    // ponytail: 类型表 = 唯一真源。加新类型只在这里加一行 + OsdWindow 里加个 Kind 常量,
    // 不用碰 XAML(8 行开关结构完全一致,复制 8 遍只会漏改)。
    static readonly (string Kind, Func<string> Title, Func<string> Desc)[] Kinds = {
      (OsdWindow.KindPreset,      () => Strings.OsdKindPreset,      () => Strings.OsdKindPresetDesc),
      (OsdWindow.KindFanMode,     () => Strings.OsdKindFanMode,     () => Strings.OsdKindFanModeDesc),
      (OsdWindow.KindPower,       () => Strings.OsdKindPower,       () => Strings.OsdKindPowerDesc),
      (OsdWindow.KindRefreshRate, () => Strings.OsdKindRefreshRate, () => Strings.OsdKindRefreshRateDesc),
      (OsdWindow.KindCpuPower,    () => Strings.OsdKindCpuPower,    () => Strings.OsdKindCpuPowerDesc),
      (OsdWindow.KindGpuClock,    () => Strings.OsdKindGpuClock,    () => Strings.OsdKindGpuClockDesc),
      (OsdWindow.KindLockKey,     () => Strings.OsdKindLockKey,     () => Strings.OsdKindLockKeyDesc),
      (OsdWindow.KindNotify,      () => Strings.OsdKindNotify,      () => Strings.OsdKindNotifyDesc),
    };

    readonly List<(string Kind, ToggleSwitch Toggle)> _toggles = new List<(string, ToggleSwitch)>();
    bool _loading;

    public OsdKindsWindow() {
      InitializeComponent();
      // ponytail: 开关即时生效,关掉窗口不会丢改动;Esc 关闭是对话框惯例(原先只能点 X)。
      KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
      Loaded += (s, e) => { BuildRows(); };
    }

    void BuildRows() {
      if (_toggles.Count > 0) return;
      _loading = true;
      foreach (var k in Kinds) {
        var toggle = new ToggleSwitch {
          IsChecked = !OsdWindow.IsKindMuted(k.Kind),
          VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Checked += (s, e) => OnToggle(k.Kind, true);
        toggle.Unchecked += (s, e) => OnToggle(k.Kind, false);
        _toggles.Add((k.Kind, toggle));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock {
          Text = k.Title(), FontSize = (double)FindResource("FontSizeSubtitle"),
          VerticalAlignment = VerticalAlignment.Center
        });
        text.Children.Add(new TextBlock {
          Text = k.Desc(), FontSize = (double)FindResource("FontSizeCaption"),
          Foreground = TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush,
          TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 12, 0)
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        // ponytail: 原来是 8 行裸文字+开关,在 460px 窗口里像一张表单;改成列表行卡片,
        // 与 StorageCleanWindow / 自动化页同一套观感(OmenListCard 已含底色/描边/圆角/间距)。
        RowsPanel.Children.Add(new Border {
          Style = (Style)FindResource("OmenListCard"),
          Child = grid
        });
      }
      _loading = false;
    }

    void OnToggle(string kind, bool enabled) {
      if (_loading) return;
      ConfigService.OsdMutedKinds = OsdWindow.SetKindMuted(ConfigService.OsdMutedKinds, kind, !enabled);
      ConfigService.Save("OsdMutedKinds");
      // ponytail: 关掉锁定键提示后轮询不必停(基线仍要同步),这里不碰 monitor —— 它每 tick 自查。
    }

    void AllOn_Click(object sender, RoutedEventArgs e) => ApplyAll(enabled: true);
    void AllOff_Click(object sender, RoutedEventArgs e) => ApplyAll(enabled: false);

    void ApplyAll(bool enabled) {
      _loading = true;
      foreach (var t in _toggles) {
        t.Toggle.IsChecked = enabled;
        ConfigService.OsdMutedKinds = OsdWindow.SetKindMuted(ConfigService.OsdMutedKinds, t.Kind, !enabled);
      }
      _loading = false;
      ConfigService.Save("OsdMutedKinds");
    }
  }
}
