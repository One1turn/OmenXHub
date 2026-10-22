// AutomationPage.cs - 自动化页面
// 流水线列表管理、快捷操作、主开关，支持触发器和步骤的增删改查
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OmenSuperHub.Services;
using OmenSuperHub.Utils;

namespace OmenSuperHub.Pages {
  public partial class AutomationPage : Page {
    public AutomationPage() {
      InitializeComponent();
      // ponytail: 执行状态事件驱动"执行中"标签刷新 —— Loaded 订/Unloaded 退订对称,与
      // CachedPageService.Clear() 的 RaiseEvent(Unloaded) 解订阅机制匹配 (eca2667 曾误删致标签变死快照)。
      Action<string> execHandler = null;
      Loaded += (s, e) => {
        bool enabled = ConfigService.AutomationEnabled;
        AutoEnableToggle.IsChecked = enabled;
        AutoAddPipelineBtn.IsEnabled = enabled;
        AutoAddQuickActionBtn.IsEnabled = enabled;
        RefreshList();
        // 审查修复: Loaded 连续两次触发而中间无 Unloaded 时,旧委托成为不可退订的孤儿
        // (页面被静态事件钉住 + 执行事件触发 N 次刷新)—— 订阅前先退订(NetworkBoostPage 同款)。
        if (execHandler != null) AutomationProcessor.ExecutionStatusChanged -= execHandler;
        execHandler = (name) => Dispatcher.InvokeAsync(() => RefreshList());
        AutomationProcessor.ExecutionStatusChanged += execHandler;
      };
      Unloaded += (s, e) => {
        if (execHandler != null) {
          AutomationProcessor.ExecutionStatusChanged -= execHandler;
          execHandler = null;
        }
      };
    }

    void AutoEnableToggle_Checked(object sender, RoutedEventArgs e) {
      ConfigService.AutomationEnabled = true;
      ConfigService.Save("AutomationEnabled");
      AutoAddPipelineBtn.IsEnabled = true;
      AutoAddQuickActionBtn.IsEnabled = true;
      UpdateEmptyText();
      // ponytail: 总开关走中央入口 ReevaluateBackendNeeded —— 它按 IsBackendWanted
      // (总开关 AND 简洁模式可见性) 决定 AutomationProcessor.Start/Stop。本回调只触发总开关,
      // 简洁模式回行为 SettingsPage.CbNavItem_Changed 负责另一边。
      AutomationProcessor.ReevaluateBackendNeeded();
      TrayService.RebuildMenu();
    }

    void AutoEnableToggle_Unchecked(object sender, RoutedEventArgs e) {
      ConfigService.AutomationEnabled = false;
      ConfigService.Save("AutomationEnabled");
      AutoAddPipelineBtn.IsEnabled = false;
      AutoAddQuickActionBtn.IsEnabled = false;
      UpdateEmptyText();
      // ponytail: 同上 — 关总开关后中央入口判 IsBackendWanted=false → Stop, 释放 WMI watcher/
      // 定时器/热键源 HwndSource。Stop 内无 _running 守卫,每次都清理成干净状态。
      AutomationProcessor.ReevaluateBackendNeeded();
      TrayService.RebuildMenu();
    }

    // ponytail: 空态文案随总开关切换 —— 关闭时按钮禁用,原『请点击新建』自相矛盾;改引批次5 的禁用提示。
    void UpdateEmptyText() {
      bool enabled = ConfigService.AutomationEnabled;
      AutoNoPipelinesLabel.Text = enabled ? Strings.AutoNoPipelinesText : Strings.AutomationEmptyDisabled;
      AutoNoQuickActionsLabel.Text = enabled ? Strings.AutoNoQuickActionsText : Strings.AutomationEmptyDisabled;
    }

    void RefreshList() {
      AutoPipelineList.Children.Clear();
      AutoNoPipelines.Visibility = Visibility.Collapsed;
      var hasPipeline = false;
      var pipelines = AutomationService.Pipelines;
      if (pipelines != null) {
        foreach (var p in pipelines) {
          // ponytail: 复用 AutomationPipeline.IsQuickAction getter —— 它自己会 EnsureTriggers()，
          // 既避免本页手写 `Triggers.Count==1 && Triggers[0].Type=="QuickAction"` 的重复,
          // 也保证 pipeline 是从老版本配置加载（Triggers 为 null）时不出 NRE。
          if (p.IsQuickAction) continue;
          hasPipeline = true;
          AutoPipelineList.Children.Add(BuildPipelineCard(p));
        }
      }
      if (!hasPipeline)
        AutoNoPipelines.Visibility = Visibility.Visible;
      UpdateEmptyText();
      RefreshQuickActions();
      TrayService.RebuildMenu();
    }

    void RefreshQuickActions() {
      AutoQuickActionPanel.Children.Clear();
      var qas = AutomationService.GetQuickActions();
      AutoNoQuickActions.Visibility = qas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
      foreach (var qa in qas)
        AutoQuickActionPanel.Children.Add(BuildQuickActionCard(qa));
    }

    FrameworkElement BuildQuickActionCard(AutomationPipeline p) {
      // ponytail: 统一走 ui:CardControl(LLT 同款)而非手搓 Border —— 手搓版要自己维护圆角/
      // 背景/内边距三份值,且不跟随主题切换的卡片样式;CardControl 已含 hover/禁用/边框语义。
      var card = new Wpf.Ui.Controls.CardControl {
        Margin = new Thickness(0, 0, 8, 8),
        MinWidth = 260,
        Header = BuildCardHeader(p, Strings.AutomationQuickActions)
      };
      return card;
    }

    FrameworkElement BuildPipelineCard(AutomationPipeline p) {
      var card = new Wpf.Ui.Controls.CardControl {
        Margin = new Thickness(0, 0, 0, 8),
        Header = BuildCardHeader(p, null)
      };
      return card;
    }

    /// <summary>卡片头部:左列 名称/触发器标签/步骤数,右列 启用开关+运行/编辑/删除。
    /// 名称与副标题上下两行,右侧按钮 RowSpan 覆盖两行垂直居中 —— LLT CardHeaderControl 的
    /// Accessory 布局约定,此处用 Grid 手写以复用现有控件而不引入基类。</summary>
    Grid BuildCardHeader(AutomationPipeline p, string subtitleOverride) {
      var grid = new Grid();
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
      grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

      var left = new StackPanel();
      left.Children.Add(new TextBlock {
        Text = p.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
      });

      // 触发器标签(流水线) / 固定副标题(快捷操作)
      if (subtitleOverride != null) {
        left.Children.Add(new TextBlock {
          Text = subtitleOverride, FontSize = 11,
          Foreground = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? SystemColors.GrayTextBrush,
          Margin = new Thickness(0, 2, 0, 0)
        });
      } else {
        p.EnsureTriggers();
        if (p.Triggers.Count > 0) {
          var triggerPanel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
          foreach (var t in p.Triggers) {
            triggerPanel.Children.Add(new Border {
              Background = TryFindResource("CardBackgroundFillColorSecondaryBrush") as Brush ?? SystemColors.ControlBrush,
              CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2),
              Margin = new Thickness(0, 0, 4, 2),
              Child = new TextBlock { Text = AutomationTriggerHelper.GetDisplayText(t), FontSize = 11 }
            });
          }
          left.Children.Add(triggerPanel);
        }
      }

      if (p.Steps.Count > 0) {
        left.Children.Add(new TextBlock {
          Text = Strings.AutomationStepCount(p.Steps.Count), FontSize = 11,
          Foreground = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? SystemColors.GrayTextBrush,
          Margin = new Thickness(0, 4, 0, 0)
        });
      }
      Grid.SetColumn(left, 0);
      grid.Children.Add(left);

      // 右侧控件组 —— RowSpan=2 跨名称/副标题两行,垂直居中
      var btnPanel = new StackPanel {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
      };

      if (subtitleOverride == null) {
        var toggle = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = p.Enabled, VerticalAlignment = VerticalAlignment.Center };
        toggle.Checked += (s, e) => { p.Enabled = true; AutomationService.Save(); };
        toggle.Unchecked += (s, e) => { p.Enabled = false; AutomationService.Save(); };
        btnPanel.Children.Add(toggle);

        if (AutomationProcessor.CurrentPipelineName == p.Name && AutomationProcessor.IsExecuting) {
          btnPanel.Children.Add(new TextBlock {
            Text = Strings.AutomationExecuting,
            // ponytail: 忙碌色走主题资源(AccentOrange),取不到回退 Brushes.Orange;不用 AccentOmen(暗色下是白,无警示感)
            Foreground = TryFindResource("AccentOrangeBrush") as Brush ?? Brushes.Orange,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
          });
        }
      }

      var runBtn = new Wpf.Ui.Controls.Button {
        Content = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Play24, FontSize = 12 },
        Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
        Width = 32, Height = 28, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(0),
        ToolTip = Strings.AutomationRunNow
      };
      runBtn.Click += (s, e) => AutomationProcessor.ExecutePipeline(p);
      btnPanel.Children.Add(runBtn);

      var editBtn = new Wpf.Ui.Controls.Button {
        Content = Strings.ButtonEdit, Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
        MinWidth = 56, Height = 28, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0)
      };
      editBtn.Click += (s, e) => ShowEditDialog(p);
      btnPanel.Children.Add(editBtn);

      var delBtn = new Wpf.Ui.Controls.Button {
        Content = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24, FontSize = 12 },
        Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
        Width = 32, Height = 28, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(0),
        ToolTip = Strings.ButtonDelete
      };
      delBtn.Click += (s, e) => { AutomationService.RemovePipeline(p); RefreshList(); };
      btnPanel.Children.Add(delBtn);

      Grid.SetColumn(btnPanel, 1);
      Grid.SetRowSpan(btnPanel, 2);
      grid.Children.Add(btnPanel);
      return grid;
    }


    void AutoAddPipeline_Click(object sender, RoutedEventArgs e) {
      ShowEditDialog(null);
    }

    void AutoAddQuickAction_Click(object sender, RoutedEventArgs e) {
      ShowEditDialog(null, true);
    }

    void ShowEditDialog(AutomationPipeline existing, bool isQuickAction = false) {
      try {
        var owner = Window.GetWindow(this);
        if (owner == null) return;
        var editor = new Views.PipelineEditorWindow(existing, owner, isQuickAction);
        if (editor.ShowDialog() == true)
          RefreshList();
      } catch (Exception ex) {
        DialogHelper.Info("Error: " + ex.Message + "\n\n" + ex.StackTrace, "Pipeline Editor Error");
      }
    }

  }

  internal static class AutomationTriggerHelper {
    public static (string type, string label)[] AllTriggerTypes = {
      ("ProcessStart", Strings.AutomationTriggerProcessStart),
      ("ProcessStop", Strings.AutomationTriggerProcessStop),
      ("Startup", Strings.AutomationTriggerStartup),
      ("Resume", Strings.AutomationTriggerResume),
      ("SessionLock", Strings.AutomationTriggerSessionLock),
      ("SessionUnlock", Strings.AutomationTriggerSessionUnlock),
      ("PowerAC", Strings.AutomationTriggerPowerAC),
      ("PowerDC", Strings.AutomationTriggerPowerDC),
      ("DisplayConnect", Strings.AutomationTriggerDisplayConnect),
      ("DisplayDisconnect", Strings.AutomationTriggerDisplayDisconnect),
      ("TimeSchedule", Strings.AutomationTriggerTimeSchedule),
      ("BatteryAbove", Strings.AutomationTriggerBatteryAbove),
      ("BatteryBelow", Strings.AutomationTriggerBatteryBelow),
       ("CpuTempAbove", Strings.AutomationTriggerCpuTempAbove),
       ("GpuTempAbove", Strings.AutomationTriggerGpuTempAbove),
       ("QuickAction", Strings.AutomationTriggerQuickAction),
       ("Hotkey", Strings.AutomationTriggerHotkey),
     };

    public static string GetDisplayText(AutomationTrigger t) {
      foreach (var a in AllTriggerTypes)
        if (a.type == t.Type) {
          if (!string.IsNullOrEmpty(t.Value))
            return a.label + ": " + t.Value;
          return a.label;
        }
      return t.Type + (string.IsNullOrEmpty(t.Value) ? "" : ": " + t.Value);
    }

    }
}
