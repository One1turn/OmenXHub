// MacroPage.cs - 键盘宏管理页面
// 宏序列的录制、播放、编辑、删除，支持触发键绑定和重复次数配置
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmenSuperHub.Services;
using OmenSuperHub.Utils;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;

namespace OmenSuperHub.Pages {
  public partial class MacroPage : Page {
    bool _capturingKey;
    Action<uint> _keyCaptureCallback;
    // ponytail: 主题刷子一次性缓存，避免每次 BuildMacroCard/ShowEditDialog 调 TryFindResource。
    static Brush _cardBg, _textSec, _borderSubtle;
    static bool _brushesCached;
    static void CacheBrushes(System.Windows.FrameworkElement fe) {
      if (_brushesCached) return;
      _cardBg = fe.TryFindResource("CardBackgroundFillColorDefaultBrush") as Brush
                ?? System.Windows.Media.Brushes.White;
      _textSec = fe.TryFindResource("TextFillColorSecondaryBrush") as Brush
                 ?? System.Windows.Media.Brushes.Gray;
      _borderSubtle = fe.TryFindResource("BorderSubtleBrush") as Brush
                      ?? System.Windows.Media.Brushes.Transparent;
      _brushesCached = true;
    }

    public MacroPage() {
      InitializeComponent();
      Loaded += (s, e) => {
        CacheBrushes(this);
        MacroMasterToggle.IsChecked = ConfigService.MacroEnabled;
        MacroController.SetEnabled(ConfigService.MacroEnabled);
        AddMacroBtn.IsEnabled = ConfigService.MacroEnabled;
        RefreshList();
      };
      Unloaded += (s, e) => {
        // ponytail: 录制中离开页面必须就地收尾 —— 否则钩子继续系统级吞掉所有按键（提示已随页
        // 隐藏，只能盲按 ESC），且事件回调已退订、Save 永不执行，重启即丢录像。
        // StopRecording 全部状态都在 UI 线程，这里调用安全。
        UnsubscribeRecordingStopped();
        if (MacroController.IsRecording) {
          MacroController.StopRecording();
          MacroService.Save();
        }
      };
    }

    // ponytail: 空态文案随总开关切换 —— 关闭时按钮禁用,原『点击上方添加』自相矛盾;改引批次5 的禁用提示。
    void UpdateEmptyText() {
      NoMacrosLabel.Text = ConfigService.MacroEnabled ? Strings.MacroNoMacros : Strings.MacroEmptyDisabled;
    }

    void RefreshList() {
      MacroList.Children.Clear();
      NoMacrosText.Visibility = Visibility.Collapsed;
      UpdateEmptyText();
      var macros = MacroService.Macros;
      if (macros == null || macros.Count == 0) {
        NoMacrosText.Visibility = Visibility.Visible;
        return;
      }
      foreach (var m in macros) {
        MacroList.Children.Add(BuildMacroCard(m));
      }
    }

    FrameworkElement BuildMacroCard(MacroSequence m) {
      // ponytail: 与 AutomationPage 的卡片同构 —— ui:CardControl + 头部两行(名称/触发键,
      // 右列开关),按钮行放 Content。原实现手搓 Border(圆角/背景/内边距自维护)且开关在
      // 头部第一列之外,和自动化卡片对不齐。
      var card = new Wpf.Ui.Controls.CardControl {
        Margin = new Thickness(0, 0, 0, 8)
      };
      TextBlock recHint;
      card.Header = BuildMacroHeader(m, out recHint);

      var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
      var recordBtn = MakeBtn(Strings.MacroRecord, SymbolRegular.Record24, null);
      recordBtn.Click += (s, e) => {
        if (MacroController.IsRecording) return;
        MacroController.StartRecording(m, true);
        recHint.Visibility = Visibility.Visible;
        recordBtn.IsEnabled = false;
        SubscribeRecordingStopped(recordBtn, recHint);
      };
      buttons.Children.Add(recordBtn);
      buttons.Children.Add(MakeBtn(Strings.MacroPlayTest, SymbolRegular.Play24, (s, e) => {
        if (!MacroController.IsPlaying)
          MacroController.PlayMacro(m);
      }));
      buttons.Children.Add(MakeBtn(Strings.ButtonEdit, SymbolRegular.Edit24, (s, e) => {
        if (ShowEditDialog(m) == true) { MacroService.Save(); RefreshList(); }
      }));
      buttons.Children.Add(MakeBtn(Strings.MacroDelete, SymbolRegular.Delete24, (s, e) => {
        if (DialogHelper.Confirm(Strings.MacroConfirmDelete, Strings.MacroConfirmDeleteTitle)) {
          MacroService.RemoveMacro(m);
          RefreshList();
        }
      }));
      card.Content = buttons;
      return card;
    }

    /// <summary>卡片头部:左列 名称 + [触发键] + 录制提示,右列 启用开关(RowSpan=2 居中)。
    /// 名称与提示同排,与 AutomationPage 的「名称 + 触发器标签」节奏一致。</summary>
    Grid BuildMacroHeader(MacroSequence m, out TextBlock recHint) {
      var header = new Grid();
      header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

      var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
      namePanel.Children.Add(new TextBlock {
        Text = m.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200
      });
      string triggerName = MacroController.GetKeyName(m.TriggerKey);
      namePanel.Children.Add(new TextBlock {
        Text = "[" + triggerName + "]", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        Foreground = _textSec, TextTrimming = TextTrimming.CharacterEllipsis
      });
      recHint = new TextBlock {
        Text = Strings.MacroRecordingCardHint, FontSize = 11, Margin = new Thickness(6, 0, 0, 0),
        // ponytail: 录制警示色走主题资源(AccentRed),取不到回退原 Brushes.OrangeRed;不用 AccentOmen(暗色下是白,无警示感)
        Foreground = TryFindResource("AccentRedBrush") as Brush ?? System.Windows.Media.Brushes.OrangeRed,
        Visibility = Visibility.Collapsed
      };
      namePanel.Children.Add(recHint);
      Grid.SetColumn(namePanel, 0);
      header.Children.Add(namePanel);

      var toggle = new ToggleSwitch {
        IsChecked = m.Enabled, Tag = m, Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center
      };
      toggle.Checked += (s, e) => {
        // 与编辑对话框保存时同款冲突拦截。x.TriggerKey != 0 不可省 —— 对话框路径有
        // m.TriggerKey != 0 外层守卫，这里没有，漏了会把无触发键的启用宏误判为冲突。
        var conflict = MacroService.Macros.Find(
          x => x != m && x.TriggerKey != 0 && x.TriggerKey == m.TriggerKey && x.Enabled);
        if (conflict != null) {
          DialogHelper.Warn(Strings.MacroTriggerConflict(conflict.Name), Strings.MacroTriggerConflictTitle);
          toggle.IsChecked = false; // 触发 Unchecked → m.Enabled=false + Save，状态自洽
          return;
        }
        m.Enabled = true;
        MacroService.Save();
      };
      toggle.Unchecked += (s, e) => { m.Enabled = false; MacroService.Save(); };
      Grid.SetColumn(toggle, 1);
      header.Children.Add(toggle);
      return header;
    }

    static Button MakeBtn(string text, Wpf.Ui.Controls.SymbolRegular icon, RoutedEventHandler click) {
      var btn = new Button {
        Margin = new Thickness(0, 0, 6, 0), Height = 28, Padding = new Thickness(8, 0, 8, 0), FontSize = 12
      };
      var inner = new StackPanel { Orientation = Orientation.Horizontal };
      inner.Children.Add(new SymbolIcon { Symbol = icon, FontSize = 12, Margin = new Thickness(0, 0, 4, 0) });
      inner.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
      btn.Content = inner;
      if (click != null) btn.Click += click;
      return btn;
    }

    // 录制结束走 MacroController.RecordingStopped 事件，替代原 50ms DispatcherTimer 轮询。
    // 事件可能在非 UI 线程触发（见 MacroController 事件注释），收尾统一切回 UI 线程。
    Action _recStoppedHandler;

    void SubscribeRecordingStopped(Button recordBtn, TextBlock recHint) {
      UnsubscribeRecordingStopped();
      Action handler = null;
      handler = () => {
        MacroController.RecordingStopped -= handler;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
          // 排队期间若已换页/开始新录制，_recStoppedHandler 已被替换或清空 —— 旧收尾必须
          // 放弃，否则 RefreshList 会拆掉新录制卡，或操作已卸载页面。
          if (_recStoppedHandler != handler) return;
          _recStoppedHandler = null;
          recHint.Visibility = Visibility.Collapsed;
          recordBtn.IsEnabled = true;
          MacroService.Save();
          RefreshList();
        }));
      };
      _recStoppedHandler = handler;
      // 先 -= 再 +=（项目内事件订阅的幂等惯例）：重复订阅同一 handler 也只触发一次。
      MacroController.RecordingStopped -= handler;
      MacroController.RecordingStopped += handler;
    }

    void UnsubscribeRecordingStopped() {
      var h = _recStoppedHandler;
      _recStoppedHandler = null;
      if (h != null) MacroController.RecordingStopped -= h;
    }

    void AddMacro_Click(object sender, RoutedEventArgs e) {
	      var m = new MacroSequence { Name = Strings.MacroNewMacro };
      if (ShowEditDialog(m) == true) {
        MacroService.AddMacro(m);
      }
      RefreshList();
    }

    bool? ShowEditDialog(MacroSequence m) {
      var win = new FluentWindow {
        Title = Strings.MacroEditTitle, Width = 460, Height = 480,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Owner = Window.GetWindow(this), ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false, WindowBackdropType = WindowBackdropType.Mica,
        ExtendsContentIntoTitleBar = true, Background = System.Windows.Media.Brushes.Transparent
      };
      // ponytail: 关闭前断开 Owner,避免 owned window 关闭把主窗口误最小化(通用弹窗 bug)
      OmenSuperHub.Utils.WindowHelper.DetachOwnerOnClose(win);
      var root = new Grid();
      root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
      root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
      root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

      // ponytail: 原来手搓标题 Border —— ExtendsContentIntoTitleBar 已抹掉原生栏,那个 Border
      // 既不能拖也没有系统关闭键。收口到 DialogHelper.MakeTitleBar。
      root.Children.Add(OmenSuperHub.Utils.DialogHelper.MakeTitleBar(Strings.MacroEditTitle, SymbolRegular.Keyboard24));

      var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 8, 12, 0) };
      var fields = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

      // Macro Name card
      fields.Children.Add(new Border {
        Background = _cardBg,
        CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 14, 12, 14), Margin = new Thickness(0, 0, 0, 8),
        Child = new Grid {
          ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } },
          Children = {
            new TextBlock { Text = Strings.MacroName, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }
          }
        }
      });
      var nameBox = new TextBox { Text = m.Name, Height = 32, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center, MinWidth = 200 };
      Grid.SetColumn(nameBox, 1);
      ((Grid)((Border)fields.Children[fields.Children.Count - 1]).Child).Children.Add(nameBox);

      // Trigger Key card
      fields.Children.Add(new Border {
        Background = _cardBg,
        CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 14, 12, 14), Margin = new Thickness(0, 0, 0, 8),
        Child = new StackPanel { Children = {
          new TextBlock { Text = Strings.MacroTriggerKey, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) }
        } }
      });
      var triggerRow = new StackPanel { Orientation = Orientation.Horizontal };
      var triggerText = new TextBlock {
        Text = MacroController.GetKeyName(m.TriggerKey), VerticalAlignment = VerticalAlignment.Center,
        MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), FontSize = 13,
        Foreground = _textSec
      };
      triggerRow.Children.Add(triggerText);
      var captureBtn = new Button {
        MinWidth = 100, Height = 28, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 0)
      };
      var captureInner = new StackPanel { Orientation = Orientation.Horizontal };
      captureInner.Children.Add(new SymbolIcon { Symbol = SymbolRegular.Keyboard24, FontSize = 12, Margin = new Thickness(0, 0, 4, 0) });
      captureInner.Children.Add(new TextBlock { Text = Strings.MacroCaptureKey, VerticalAlignment = VerticalAlignment.Center });
      captureBtn.Content = captureInner;
      captureBtn.Click += (s, e) => {
        _capturingKey = true;
        _keyCaptureCallback = (vk) => {
          m.TriggerKey = vk;
          triggerText.Text = MacroController.GetKeyName(vk);
          _capturingKey = false;
        };
      };
      triggerRow.Children.Add(captureBtn);
      var clearBtn = new Button {
        MinWidth = 60, Height = 28, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 0)
      };
      var clearInner = new StackPanel { Orientation = Orientation.Horizontal };
      clearInner.Children.Add(new SymbolIcon { Symbol = SymbolRegular.Delete24, FontSize = 12, Margin = new Thickness(0, 0, 4, 0) });
      clearInner.Children.Add(new TextBlock { Text = Strings.MacroClearKey, VerticalAlignment = VerticalAlignment.Center });
      clearBtn.Content = clearInner;
	      clearBtn.Click += (s, e) => { m.TriggerKey = 0; triggerText.Text = Strings.MacroNone; };
      triggerRow.Children.Add(clearBtn);
      var eventsText = new TextBlock {
        Text = Strings.MacroEventsCount(m.Events.Count), VerticalAlignment = VerticalAlignment.Center,
        FontSize = 11, Foreground = _textSec
      };
      triggerRow.Children.Add(eventsText);
      ((StackPanel)((Border)fields.Children[fields.Children.Count - 1]).Child).Children.Add(triggerRow);

      // Repeat Count card
      fields.Children.Add(new Border {
        Background = _cardBg,
        CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 14, 12, 14), Margin = new Thickness(0, 0, 0, 8),
        Child = new Grid {
          ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } },
          Children = {
            new TextBlock { Text = Strings.MacroRepeatCount, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }
          }
        }
      });
      var repeatBox = new TextBox { Text = m.RepeatCount.ToString(), Width = 80, Height = 32, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
      Grid.SetColumn(repeatBox, 1);
      ((Grid)((Border)fields.Children[fields.Children.Count - 1]).Child).Children.Add(repeatBox);

      // Options card
      fields.Children.Add(new Border {
        Background = _cardBg,
        CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 14, 12, 14), Margin = new Thickness(0, 0, 0, 8),
        Child = new StackPanel { Children = {
          new Grid {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
            Children = {
              new TextBlock { Text = Strings.MacroIgnoreDelays, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }
            }
          }
        } }
      });
      var ignoreToggle = new ToggleSwitch { IsChecked = m.IgnoreDelays, Margin = new Thickness(8, 0, 0, 0) };
      Grid.SetColumn(ignoreToggle, 1);
      ((Grid)((StackPanel)((Border)fields.Children[fields.Children.Count - 1]).Child).Children[0]).Children.Add(ignoreToggle);

      var secondOpt = new Grid {
        Margin = new Thickness(0, 8, 0, 0),
        ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } }
      };
      secondOpt.Children.Add(new TextBlock { Text = Strings.MacroInterruptOnOtherKey, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
      var interruptToggle = new ToggleSwitch { IsChecked = m.InterruptOnOtherKey, Margin = new Thickness(8, 0, 0, 0) };
      Grid.SetColumn(interruptToggle, 1);
      secondOpt.Children.Add(interruptToggle);
      ((StackPanel)((Border)fields.Children[fields.Children.Count - 1]).Child).Children.Add(secondOpt);

      scroll.Content = fields;
      Grid.SetRow(scroll, 1);
      root.Children.Add(scroll);

      // Button bar
      var btnBar = new Border {
        BorderBrush = _borderSubtle,
        BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 10, 16, 10)
      };
      var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
      var saveBtn = new Button {
        MinWidth = 80, Height = 30, Margin = new Thickness(0, 0, 8, 0), FontSize = 13
      };
      var saveInner = new StackPanel { Orientation = Orientation.Horizontal };
      saveInner.Children.Add(new SymbolIcon { Symbol = SymbolRegular.Checkmark24, FontSize = 12, Margin = new Thickness(0, 0, 4, 0) });
      saveInner.Children.Add(new TextBlock { Text = Strings.ButtonSave, VerticalAlignment = VerticalAlignment.Center });
      saveBtn.Content = saveInner;
      saveBtn.Click += (s, e) => {
        m.Name = nameBox.Text;
        if (int.TryParse(repeatBox.Text, out int r) && r >= 1 && r <= 10)
          m.RepeatCount = r;
        m.IgnoreDelays = ignoreToggle.IsChecked == true;
        m.InterruptOnOtherKey = interruptToggle.IsChecked == true;
        // Trigger-key conflict: prevent silent shadowing of an enabled macro sharing the same key.
        if (m.TriggerKey != 0 && m.Enabled) {
          var conflict = MacroService.Macros.Find(x => x != m && x.TriggerKey == m.TriggerKey && x.Enabled);
          if (conflict != null) {
            DialogHelper.Warn(Strings.MacroTriggerConflict(conflict.Name), Strings.MacroTriggerConflictTitle);
            return;
          }
        }
        win.DialogResult = true;
      };
      btnPanel.Children.Add(saveBtn);
      var cancelBtn = new Button { MinWidth = 80, Height = 30, FontSize = 13 };
      var cancelInner = new StackPanel { Orientation = Orientation.Horizontal };
      cancelInner.Children.Add(new SymbolIcon { Symbol = SymbolRegular.Delete24, FontSize = 12, Margin = new Thickness(0, 0, 4, 0) });
      cancelInner.Children.Add(new TextBlock { Text = Strings.ButtonCancel, VerticalAlignment = VerticalAlignment.Center });
      cancelBtn.Content = cancelInner;
      cancelBtn.Click += (s, e) => win.DialogResult = false;
      btnPanel.Children.Add(cancelBtn);
      btnBar.Child = btnPanel;
      Grid.SetRow(btnBar, 2);
      root.Children.Add(btnBar);

      win.Content = root;

      System.Windows.Input.KeyEventHandler handler = null;
      handler = (s, e) => {
        if (_capturingKey && _keyCaptureCallback != null) {
          uint vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(e.Key);
          _keyCaptureCallback(vk);
          _keyCaptureCallback = null;
          e.Handled = true;
        }
      };
      win.KeyDown += handler;
      // 审查修复: 捕获中途直接关窗(点取消/标题栏)不复位 —— 残留的 _keyCaptureCallback
      // 指向上一个宏对象,下个对话框里按第一个键会把触发键写进上一个宏并吞掉该按键。
      win.Closed += (s, e) => { _capturingKey = false; _keyCaptureCallback = null; };
      return win.ShowDialog();
    }

    void     MacroMasterToggle_Checked(object sender, RoutedEventArgs e) {
      ConfigService.MacroEnabled = true;
      ConfigService.Save("MacroEnabled");
      MacroController.SetEnabled(true);
      AddMacroBtn.IsEnabled = true;
      UpdateEmptyText();
      // ponytail: 真装全局键盘钩子 (SetWindowsHookEx WH_KEYBOARD_LL)。Start 已加幂等守卫,
      // App 启动已 Start 过时这里是 no-op,关后再开是真正的新装。SetEnabled(_enabled=true)
      // 仍保留 —— 它只是钩子回调里"按键是否触发 PlayMacro"的开关,跟"装没装钩子"独立。
      MacroController.Start();
    }

    void MacroMasterToggle_Unchecked(object sender, RoutedEventArgs e) {
      ConfigService.MacroEnabled = false;
      ConfigService.Save("MacroEnabled");
      MacroController.SetEnabled(false);
      AddMacroBtn.IsEnabled = false;
      UpdateEmptyText();
      // ponytail: 真卸全局键盘钩子 (UnhookWindowsHookEx + 释放播放 CTS)。卸钩后再次 Start
      // 是真正重装。SetEnabled(false) 同时也关了"按键触发宏"的门,保证 Stop 之间最后一拍
      // 不会触发正在录制的宏。
      MacroController.Stop();
    }

  }
}
