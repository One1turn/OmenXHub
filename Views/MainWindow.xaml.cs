using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using OmenSuperHub.Services;
using static OmenSuperHub.OmenHardware;

namespace OmenSuperHub.Views {
  public partial class MainWindow : Window {
    static MainWindow _instance;
    DispatcherTimer _refreshTimer;
    bool _loading = true;  // suppress events during init

    // Fan curve state
    List<(float temp, int rpm)> _curvePoints;
    int _draggingIndex = -1;
    const int CurvePointRadius = 7;
    const float MaxRPM = 6400f;
    const float MinTemp = 0f;
    const float MaxTemp = 100f;

    public MainWindow() {
      InitializeComponent();

      // Version
      Version v = Assembly.GetExecutingAssembly().GetName().Version;
      VersionText.Text = "v" + v.ToString();

      // Build dynamic radio buttons
      BuildFanControlOptions();
      BuildCpuPowerOptions();
      BuildGpuPowerOptions();
      BuildGpuClockOptions();

      // Load current config into UI
      LoadConfigState();

      _loading = false;

      // Refresh timer for dashboard
      _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
      _refreshTimer.Tick += (s, e) => RefreshDashboard();
      _refreshTimer.Start();

      RefreshDashboard();

      Closing += (s, e) => {
        e.Cancel = true;
        Hide();
      };
    }

    // ═══════════════════════════════════════════════════════
    // Singleton
    // ═══════════════════════════════════════════════════════
    public static void ShowInstance() {
      Application.Current?.Dispatcher.Invoke(() => {
        if (_instance == null || !_instance.IsLoaded) {
          _instance = new MainWindow();
        }
        _instance.Show();
        _instance.Activate();
      });
    }

    public static void CloseInstance() {
      Application.Current?.Dispatcher.Invoke(() => {
        _instance?.Close();
        _instance = null;
      });
    }

    // ═══════════════════════════════════════════════════════
    // Page Navigation
    // ═══════════════════════════════════════════════════════
    void NavButton_Checked(object sender, RoutedEventArgs e) {
      if (PageDashboard == null) return;
      PageDashboard.Visibility = Visibility.Collapsed;
      PageFan.Visibility = Visibility.Collapsed;
      PagePerf.Visibility = Visibility.Collapsed;
      PageSettings.Visibility = Visibility.Collapsed;

      if (sender == NavDashboard) PageDashboard.Visibility = Visibility.Visible;
      else if (sender == NavFan) PageFan.Visibility = Visibility.Visible;
      else if (sender == NavPerf) PagePerf.Visibility = Visibility.Visible;
      else if (sender == NavSettings) PageSettings.Visibility = Visibility.Visible;
    }

    // ═══════════════════════════════════════════════════════
    // Dashboard Refresh
    // ═══════════════════════════════════════════════════════
    Brush GetTempBrush(int temp) {
      if (temp >= 80) return FindResource("AccentRedBrush") as Brush;
      if (temp >= 60) return FindResource("AccentYellowBrush") as Brush;
      return FindResource("TextPrimaryBrush") as Brush;
    }

    void RefreshDashboard() {
      int cpuTemp = (int)HardwareService.CPUTemp;
      CpuTempText.Text = cpuTemp.ToString();
      CpuTempText.Foreground = GetTempBrush(cpuTemp);
      CpuFanText.Text = "风扇: " + (HardwareService.FanSpeedNow[0] * 100) + " RPM";
      CpuPowerText.Text = "功耗: " + HardwareService.CPUPower.ToString("F1") + " W";

      if (ConfigService.MonitorGPU) {
        int gpuTemp = (int)HardwareService.GPUTemp;
        GpuTempText.Text = gpuTemp.ToString();
        GpuTempText.Foreground = GetTempBrush(gpuTemp);
        GpuFanText.Text = "风扇: " + (HardwareService.FanSpeedNow[1] * 100) + " RPM";
        GpuPowerText.Text = "功耗: " + HardwareService.GPUPower.ToString("F1") + " W";
      } else {
        GpuTempText.Text = "--";
        GpuFanText.Text = "风扇: --";
        GpuPowerText.Text = "GPU监控已关闭";
      }

      // Quick status
      CurrentModeText.Text = ConfigService.FanMode == "performance" ? "狂暴模式" : "平衡模式";
      CurrentModeText.Foreground = ConfigService.FanMode == "performance"
        ? (FindResource("AccentRedBrush") as Brush)
        : (FindResource("AccentGreenBrush") as Brush);

      CurrentFanText.Text = ConfigService.FanControl == "auto" ? "自动" :
                            ConfigService.FanControl == "max" ? "最大风扇" :
                            ConfigService.FanControl == "custom" ? "自定义曲线" : ConfigService.FanControl;

      PowerStatusText.Text = HardwareService.PowerOnline ? "交流电源" : "电池";
      PowerStatusText.Foreground = HardwareService.PowerOnline
        ? (FindResource("AccentGreenBrush") as Brush)
        : (FindResource("AccentYellowBrush") as Brush);

      // Sync dashboard perf mode radios
      if (ConfigService.FanMode == "performance") {
        if (DashModePerformance.IsChecked != true) DashModePerformance.IsChecked = true;
      } else {
        if (DashModeDefault.IsChecked != true) DashModeDefault.IsChecked = true;
      }

      // Update temp indicator on curve
      if (FanCurveCard.Visibility == Visibility.Visible && _curvePoints != null) {
        DrawFanCurve();
      }
    }

    // ═══════════════════════════════════════════════════════
    // Load Config into UI
    // ═══════════════════════════════════════════════════════
    void LoadConfigState() {
      // Fan mode (unified: silent/cool/custom/manual)
      string fc = ConfigService.FanControl;
      if (fc == "custom") {
        FanModeCustom.IsChecked = true;
      } else if (fc == "" || fc == "auto" || fc == "silent" || fc == "cool") {
        // auto maps to the current fan table
        if (ConfigService.FanTable == "cool") FanModeCool.IsChecked = true;
        else FanModeSilent.IsChecked = true;
      } else {
        // max or RPM → manual mode
        FanModeManual.IsChecked = true;
      }
      // Show/hide cards based on mode (without triggering events since _loading is true)
      UpdateFanModeUI();

      // Performance mode
      if (ConfigService.FanMode == "performance") {
        ModePerformance.IsChecked = true;
        DashModePerformance.IsChecked = true;
      } else {
        ModeDefault.IsChecked = true;
        DashModeDefault.IsChecked = true;
      }

      // GPU power
      string gp = ConfigService.GpuPower;
      if (gp == "max") {
        SelectComboItem(CtgpCombo, "开启");
        SelectComboItem(DbPowerCombo, "开启");
        DbPowerCombo.IsEnabled = true;
      } else if (gp == "med") {
        SelectComboItem(CtgpCombo, "开启");
        SelectComboItem(DbPowerCombo, "关闭");
        DbPowerCombo.IsEnabled = true;
      } else {
        SelectComboItem(CtgpCombo, "关闭");
        SelectComboItem(DbPowerCombo, "关闭");
        DbPowerCombo.IsEnabled = false;
      }

      // DB version
      if (ConfigService.DBVersion == 1) DbUnlock.IsChecked = true;
      else DbNormal.IsChecked = true;

      // CPU power
      string cpuVal = ConfigService.CpuPower == "" || ConfigService.CpuPower == "max" ? "最大" : ConfigService.CpuPower;
      SelectComboItem(CpuPowerCombo, cpuVal);

      // GPU clock
      string gpuClockVal = ConfigService.GpuClock == 0 ? "还原" : ConfigService.GpuClock + " MHz";
      SelectComboItem(GpuClockCombo, gpuClockVal);

      // Monitoring (always on, no toggle)
      ConfigService.MonitorGPU = true;
      ConfigService.MonitorFan = true;

      // Floating
      FloatingToggle.IsChecked = ConfigService.FloatingBar == "on";
      switch (ConfigService.TextSize) {
        case 24: FloatSize24.IsChecked = true; break;
        case 36: FloatSize36.IsChecked = true; break;
        default: FloatSize48.IsChecked = true; break;
      }
      if (ConfigService.FloatingBarLoc == "right") FloatLocRight.IsChecked = true;
      else FloatLocLeft.IsChecked = true;

      // Omen key
      switch (ConfigService.OmenKey) {
        case "default": OmenKeyDefault.IsChecked = true; break;
        case "custom": OmenKeyCustom.IsChecked = true; break;
        case "none": OmenKeyNone.IsChecked = true; break;
        default: OmenKeyDefault.IsChecked = true; break;
      }

      // Icon
      switch (ConfigService.CustomIcon) {
        case "custom": IconCustom.IsChecked = true; break;
        case "dynamic": IconDynamic.IsChecked = true; break;
        default: IconOriginal.IsChecked = true; break;
      }

      // Auto start
      AutoStartToggle.IsChecked = ConfigService.AutoStart == "on";
    }

    void SetRadioInPanel(WrapPanel panel, string value) {
      foreach (var child in panel.Children) {
        if (child is RadioButton rb && rb.Content?.ToString() == value) {
          rb.IsChecked = true;
          return;
        }
      }
      // Default to first if not found
      if (panel.Children.Count > 0 && panel.Children[0] is RadioButton first)
        first.IsChecked = true;
    }

    // ═══════════════════════════════════════════════════════
    // Build Dynamic RadioButtons
    // ═══════════════════════════════════════════════════════
    void BuildFanControlOptions() {
      var style = FindResource("OmenOptionRadio") as Style;
      string[] labels = { "自动", "最大风扇" };
      foreach (string label in labels) {
        var rb = new RadioButton { Content = label, Style = style, GroupName = "FanCtrl" };
        rb.Checked += FanControl_Checked;
        FanControlPanel.Children.Add(rb);
      }
      for (int speed = 1600; speed <= 6400; speed += 400) {
        var rb = new RadioButton { Content = speed + " RPM", Style = style, GroupName = "FanCtrl", Tag = speed };
        rb.Checked += FanControl_Checked;
        FanControlPanel.Children.Add(rb);
      }
    }

    void BuildCpuPowerOptions() {
      CpuPowerCombo.Items.Add(new ComboBoxItem { Content = "最大" });
      for (int p = 10; p <= 120; p += 10) {
        CpuPowerCombo.Items.Add(new ComboBoxItem { Content = p + " W", Tag = p });
      }
    }

    void BuildGpuPowerOptions() {
      CtgpCombo.Items.Add(new ComboBoxItem { Content = "开启" });
      CtgpCombo.Items.Add(new ComboBoxItem { Content = "关闭" });

      DbPowerCombo.Items.Add(new ComboBoxItem { Content = "开启" });
      DbPowerCombo.Items.Add(new ComboBoxItem { Content = "关闭" });
    }

    void BuildGpuClockOptions() {
      GpuClockCombo.Items.Add(new ComboBoxItem { Content = "还原" });
      int[] clocks = { 600, 1000, 1400, 1550, 1700, 1850, 2000, 2100, 2200, 2300, 2400, 2500 };
      foreach (int c in clocks) {
        GpuClockCombo.Items.Add(new ComboBoxItem { Content = c + " MHz", Tag = c });
      }
    }

    void SelectComboItem(ComboBox combo, string text) {
      foreach (ComboBoxItem item in combo.Items) {
        if (item.Content.ToString() == text) {
          combo.SelectedItem = item;
          return;
        }
      }
      if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    // ═══════════════════════════════════════════════════════
    // Event Handlers - Fan
    // ═══════════════════════════════════════════════════════
    void FanMode_Changed(object sender, RoutedEventArgs e) {
      if (_loading) return;
      if (sender == FanModeSilent) {
        ConfigService.FanTable = "silent";
        ConfigService.FanControl = "auto";
        FanService.LoadFanConfig("silent.txt");
        SetMaxFanSpeedOff();
        TrayService.fanControlTimer.Change(0, 1000);
      } else if (sender == FanModeCool) {
        ConfigService.FanTable = "cool";
        ConfigService.FanControl = "auto";
        FanService.LoadFanConfig("cool.txt");
        SetMaxFanSpeedOff();
        TrayService.fanControlTimer.Change(0, 1000);
      } else if (sender == FanModeCustom) {
        ConfigService.FanControl = "custom";
        SetMaxFanSpeedOff();
        LoadCurvePoints();
        ApplyCustomCurve();
        TrayService.fanControlTimer.Change(0, 1000);
      } else if (sender == FanModeManual) {
        // Keep current FanControl or default to auto
        string fc = ConfigService.FanControl;
        if (fc == "custom" || fc == "") {
          ConfigService.FanControl = "auto";
        }
        // Restore the manual speed setting
        RestoreManualFanControl();
      }
      UpdateFanModeUI();
      ConfigService.Save("FanTable");
      ConfigService.Save("FanControl");
    }

    void UpdateFanModeUI() {
      bool isCustom = FanModeCustom.IsChecked == true;
      bool isManual = FanModeManual.IsChecked == true;
      FanCurveCard.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
      ManualControlCard.Visibility = isManual ? Visibility.Visible : Visibility.Collapsed;
      TempSensCard.Visibility = isManual ? Visibility.Collapsed : Visibility.Visible;

      if (isCustom) {
        LoadCurvePoints();
      }
      if (isManual) {
        // Select the right radio in FanControlPanel
        string fc = ConfigService.FanControl;
        string display = fc;
        if (fc == "" || fc == "auto") display = "自动";
        else if (fc == "max") display = "最大风扇";
        SetRadioInPanel(FanControlPanel, display);
      }
      // Sensitivity
      switch (ConfigService.TempSensitivity) {
        case "realtime": SensRealtime.IsChecked = true; break;
        case "high": SensHigh.IsChecked = true; break;
        case "medium": SensMedium.IsChecked = true; break;
        case "low": SensLow.IsChecked = true; break;
        default: SensHigh.IsChecked = true; break;
      }
    }

    void RestoreManualFanControl() {
      string fc = ConfigService.FanControl;
      if (fc == "auto") {
        SetMaxFanSpeedOff();
        TrayService.fanControlTimer.Change(0, 1000);
      } else if (fc == "max") {
        SetMaxFanSpeedOn();
        TrayService.fanControlTimer.Change(Timeout.Infinite, Timeout.Infinite);
      } else if (fc.Contains(" RPM")) {
        SetMaxFanSpeedOff();
        TrayService.fanControlTimer.Change(Timeout.Infinite, Timeout.Infinite);
        int rpmValue = int.Parse(fc.Replace(" RPM", "").Trim());
        SetFanLevel(rpmValue / 100, rpmValue / 100);
      } else {
        // Default to auto
        ConfigService.FanControl = "auto";
        SetMaxFanSpeedOff();
        TrayService.fanControlTimer.Change(0, 1000);
      }
    }

    void FanControl_Checked(object sender, RoutedEventArgs e) {
      if (_loading) return;
      var rb = sender as RadioButton;
      string label = rb?.Content?.ToString();
      if (label == "自动") {
        ConfigService.FanControl = "auto";
        SetMaxFanSpeedOff();
        TrayService.fanControlTimer.Change(0, 1000);
      } else if (label == "最大风扇") {
        ConfigService.FanControl = "max";
        SetMaxFanSpeedOn();
        TrayService.fanControlTimer.Change(Timeout.Infinite, Timeout.Infinite);
      } else if (rb?.Tag is int speed) {
        ConfigService.FanControl = speed + " RPM";
        SetMaxFanSpeedOff();
        TrayService.fanControlTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SetFanLevel(speed / 100, speed / 100);
      }
      ConfigService.Save("FanControl");
    }

    void Sensitivity_Checked(object sender, RoutedEventArgs e) {
      if (_loading) return;
      if (sender == SensRealtime) { ConfigService.TempSensitivity = "realtime"; HardwareService.RespondSpeed = 1; }
      else if (sender == SensHigh) { ConfigService.TempSensitivity = "high"; HardwareService.RespondSpeed = 0.4f; }
      else if (sender == SensMedium) { ConfigService.TempSensitivity = "medium"; HardwareService.RespondSpeed = 0.1f; }
      else if (sender == SensLow) { ConfigService.TempSensitivity = "low"; HardwareService.RespondSpeed = 0.04f; }
      ConfigService.Save("TempSensitivity");
    }

    // ═══════════════════════════════════════════════════════
    // Event Handlers - Performance
    // ═══════════════════════════════════════════════════════
    void FanMode_Checked(object sender, RoutedEventArgs e) {
      if (_loading) return;
      if (sender == ModePerformance) {
        ConfigService.FanMode = "performance";
        SetFanMode(0x31);
        DashModePerformance.IsChecked = true;
      } else {
        ConfigService.FanMode = "default";
        SetFanMode(0x30);
        DashModeDefault.IsChecked = true;
      }
      ConfigService.Save("FanMode");
      TrayService.RestoreCPUPower();
    }

    void DashPerfMode_Checked(object sender, RoutedEventArgs e) {
      if (_loading) return;
      if (sender == DashModePerformance) {
        ConfigService.FanMode = "performance";
        SetFanMode(0x31);
        ModePerformance.IsChecked = true;
      } else {
        ConfigService.FanMode = "default";
        SetFanMode(0x30);
        ModeDefault.IsChecked = true;
      }
      ConfigService.Save("FanMode");
      TrayService.RestoreCPUPower();
    }

    void CtgpCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
      if (_loading) return;
      var item = CtgpCombo.SelectedItem as ComboBoxItem;
      if (item == null) return;
      
      bool ctgpOn = item.Content.ToString() == "开启";
      if (ctgpOn) {
        DbPowerCombo.IsEnabled = true;
        // Restore DB state or default to off? Let's check current DB combo
        var dbItem = DbPowerCombo.SelectedItem as ComboBoxItem;
        bool dbOn = dbItem?.Content?.ToString() == "开启";
        ConfigService.GpuPower = dbOn ? "max" : "med";
        if (dbOn) SetMaxGpuPower(); else SetMedGpuPower();
      } else {
        DbPowerCombo.IsEnabled = false;
        SelectComboItem(DbPowerCombo, "关闭");
        ConfigService.GpuPower = "min";
        SetMinGpuPower();
      }
      ConfigService.Save("GpuPower");
    }

    void DbPowerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
      if (_loading) return;
      // If disabled, ignore (handled by Ctgp)
      if (!DbPowerCombo.IsEnabled) return;

      var item = DbPowerCombo.SelectedItem as ComboBoxItem;
      if (item == null) return;

      bool dbOn = item.Content.ToString() == "开启";
      // We know CTGP must be on to be here
      ConfigService.GpuPower = dbOn ? "max" : "med";
      if (dbOn) SetMaxGpuPower(); else SetMedGpuPower();
      ConfigService.Save("GpuPower");
    }

    void DbVersion_Checked(object sender, RoutedEventArgs e) {
      if (_loading) return;
      if (sender == DbUnlock) {
        if (!HardwareService.PowerOnline) {
          MessageBox.Show("请连接交流电源", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
          DbNormal.IsChecked = true;
          return;
        }
        if (!TrayService.CheckDBVersion(1)) {
          DbNormal.IsChecked = true;
          return;
        }
        SetFanMode(0x31);
        SetMaxGpuPower();
        SetCpuPowerLimit((byte)TrayService.CPULimitDB);
        ConfigService.DBVersion = 1;
        TrayService.ChangeDBVersion(ConfigService.DBVersion);
        TrayService.countDB = TrayService.countDBInit;
      } else {
        ConfigService.DBVersion = 2;
        TrayService.countDB = 0;
        string deviceId = "\"ACPI\\\\NVDA0820\\\\NPCF\"";
        TrayService.ExecuteCommand($"pnputil /enable-device {deviceId}");
      }
      ConfigService.Save("DBVersion");
    }

    void CpuPower_SelectionChanged(object sender, SelectionChangedEventArgs e) {
      if (_loading) return;
      var item = CpuPowerCombo.SelectedItem as ComboBoxItem;
      if (item == null) return;
      if (item.Content.ToString() == "最大") {
        ConfigService.CpuPower = "max";
        SetCpuPowerLimit(254);
      } else if (item.Tag is int power) {
        ConfigService.CpuPower = power + " W";
        SetCpuPowerLimit((byte)power);
      }
      ConfigService.Save("CpuPower");
    }

    void GpuClock_SelectionChanged(object sender, SelectionChangedEventArgs e) {
      if (_loading) return;
      var item = GpuClockCombo.SelectedItem as ComboBoxItem;
      if (item == null) return;
      if (item.Content.ToString() == "还原") {
        ConfigService.GpuClock = 0;
      } else if (item.Tag is int clock) {
        ConfigService.GpuClock = clock;
      }
      TrayService.SetGPUClockLimit(ConfigService.GpuClock);
      ConfigService.Save("GpuClock");
    }

    // ═══════════════════════════════════════════════════════
    // Event Handlers - Settings
    // ═══════════════════════════════════════════════════════
    void FloatingToggle_Changed(object sender, RoutedEventArgs e) {
      if (_loading) return;
      if (FloatingToggle.IsChecked == true) {
        ConfigService.FloatingBar = "on";
        FloatingWindow.ShowInstance();
      } else {
        ConfigService.FloatingBar = "off";
        FloatingWindow.CloseInstance();
      }
      ConfigService.Save("FloatingBar");
    }

    void FloatSize_Checked(object sender, RoutedEventArgs e) {
      if (_loading) return;
      if (sender == FloatSize24) ConfigService.TextSize = 24;
      else if (sender == FloatSize36) ConfigService.TextSize = 36;
      else ConfigService.TextSize = 48;
      FloatingWindow.UpdateText();
      ConfigService.Save("FloatingBarSize");
    }

    void FloatLoc_Checked(object sender, RoutedEventArgs e) {
      if (_loading) return;
      ConfigService.FloatingBarLoc = sender == FloatLocLeft ? "left" : "right";
      FloatingWindow.UpdateText();
      ConfigService.Save("FloatingBarLoc");
    }

    void OmenKey_Checked(object sender, RoutedEventArgs e) {
      if (_loading) return;
      if (sender == OmenKeyDefault) {
        ConfigService.OmenKey = "default";
        TrayService.checkFloatingTimer.IsEnabled = false;
        OmenKeyOff();
        OmenKeyOn(ConfigService.OmenKey);
      } else if (sender == OmenKeyCustom) {
        ConfigService.OmenKey = "custom";
        TrayService.checkFloatingTimer.IsEnabled = true;
        OmenKeyOff();
        OmenKeyOn(ConfigService.OmenKey);
      } else {
        ConfigService.OmenKey = "none";
        TrayService.checkFloatingTimer.IsEnabled = false;
        OmenKeyOff();
      }
      ConfigService.Save("OmenKey");
    }

    void TrayIcon_Checked(object sender, RoutedEventArgs e) {
      if (_loading) return;
      if (sender == IconOriginal) {
        ConfigService.CustomIcon = "original";
        TrayService.TrayIcon.Icon = Properties.Resources.smallfan;
      } else if (sender == IconCustom) {
        ConfigService.CustomIcon = "custom";
        TrayService.SetCustomIcon();
      } else {
        ConfigService.CustomIcon = "dynamic";
        TrayService.GenerateDynamicIcon((int)HardwareService.CPUTemp);
      }
      ConfigService.Save("CustomIcon");
    }

    void AutoStartToggle_Changed(object sender, RoutedEventArgs e) {
      if (_loading) return;
      if (AutoStartToggle.IsChecked == true) {
        ConfigService.AutoStart = "on";
        TrayService.AutoStartEnable();
      } else {
        ConfigService.AutoStart = "off";
        TrayService.AutoStartDisable();
      }
      ConfigService.Save("AutoStart");
    }

    // ═══════════════════════════════════════════════════════
    // Custom Window Controls
    // ═══════════════════════════════════════════════════════
    void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) {
      if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) {
        DragMove();
      }
    }

    void MinimizeButton_Click(object sender, RoutedEventArgs e) {
      WindowState = WindowState.Minimized;
    }

    void CloseButton_Click(object sender, RoutedEventArgs e) {
       Close();
    }

    // ═══════════════════════════════════════════════════════
    // Custom Fan Curve
    // ═══════════════════════════════════════════════════════
    void LoadCurvePoints() {
      var existing = FanService.LoadCustomCurve();
      if (existing != null && existing.Count > 0) {
        _curvePoints = existing;
      } else {
        _curvePoints = new List<(float, int)> {
          (20f, 0), (40f, 1600), (55f, 2200), (70f, 3400), (85f, 4800), (100f, 6400)
        };
      }
      DrawFanCurve();
    }

    void ApplyCustomCurve() {
      FanService.ApplyCustomCurve(_curvePoints);
    }

    void DrawFanCurve() {
      FanCurveCanvas.Children.Clear();
      double w = FanCurveCanvas.ActualWidth;
      double h = FanCurveCanvas.ActualHeight;
      if (w <= 0 || h <= 0) {
        // Canvas not yet measured, defer
        FanCurveCanvas.Dispatcher.BeginInvoke(new Action(() => {
          FanCurveCanvas.UpdateLayout();
          w = FanCurveCanvas.ActualWidth;
          h = FanCurveCanvas.ActualHeight;
          if (w > 0 && h > 0) DrawFanCurveInternal(w, h);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
        return;
      }
      DrawFanCurveInternal(w, h);
    }

    void DrawFanCurveInternal(double w, double h) {
      FanCurveCanvas.Children.Clear();
      var gridBrush = FindResource("BorderDefaultBrush") as Brush ?? Brushes.Gray;
      var lineBrush = FindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
      var accentBrush = FindResource("AccentOmenBrush") as Brush ?? Brushes.White;
      var mutedBrush = FindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;

      double padL = 5, padR = 5, padT = 10, padB = 20;
      double chartW = w - padL - padR;
      double chartH = h - padT - padB;

      // Grid lines + labels
      for (int t = 0; t <= 100; t += 20) {
        double x = padL + (t - MinTemp) / (MaxTemp - MinTemp) * chartW;
        var gridLine = new Line {
          X1 = x, Y1 = padT, X2 = x, Y2 = padT + chartH,
          Stroke = gridBrush, StrokeThickness = 0.5, StrokeDashArray = new DoubleCollection { 4, 4 }
        };
        FanCurveCanvas.Children.Add(gridLine);
        var label = new TextBlock {
          Text = t + "°", FontSize = 10, Foreground = mutedBrush
        };
        Canvas.SetLeft(label, x - 10);
        Canvas.SetTop(label, padT + chartH + 3);
        FanCurveCanvas.Children.Add(label);
      }

      for (int rpm = 0; rpm <= (int)MaxRPM; rpm += 1600) {
        double y = padT + chartH - (rpm / MaxRPM) * chartH;
        var gridLine = new Line {
          X1 = padL, Y1 = y, X2 = padL + chartW, Y2 = y,
          Stroke = gridBrush, StrokeThickness = 0.5, StrokeDashArray = new DoubleCollection { 4, 4 }
        };
        FanCurveCanvas.Children.Add(gridLine);
        // Y label drawn outside in the XAML StackPanel, but we add small inline labels
        var label = new TextBlock {
          Text = rpm.ToString(), FontSize = 9, Foreground = mutedBrush
        };
        Canvas.SetLeft(label, padL - 3);
        Canvas.SetTop(label, y - 12);
        FanCurveCanvas.Children.Add(label);
      }

      if (_curvePoints == null || _curvePoints.Count == 0) return;

      // Draw polyline
      var sorted = _curvePoints.OrderBy(p => p.temp).ToList();
      var polyline = new Polyline {
        Stroke = lineBrush, StrokeThickness = 2,
        StrokeLineJoin = PenLineJoin.Round
      };
      foreach (var pt in sorted) {
        double x = padL + (pt.temp - MinTemp) / (MaxTemp - MinTemp) * chartW;
        double y = padT + chartH - (pt.rpm / MaxRPM) * chartH;
        polyline.Points.Add(new Point(x, y));
      }
      FanCurveCanvas.Children.Add(polyline);

      // Draw fill area under curve
      var fillPoints = new PointCollection();
      foreach (var pt in sorted) {
        double x = padL + (pt.temp - MinTemp) / (MaxTemp - MinTemp) * chartW;
        double y = padT + chartH - (pt.rpm / MaxRPM) * chartH;
        fillPoints.Add(new Point(x, y));
      }
      // Close the polygon at the bottom
      fillPoints.Add(new Point(padL + (sorted.Last().temp - MinTemp) / (MaxTemp - MinTemp) * chartW, padT + chartH));
      fillPoints.Add(new Point(padL + (sorted.First().temp - MinTemp) / (MaxTemp - MinTemp) * chartW, padT + chartH));
      var fill = new Polygon {
        Points = fillPoints,
        Fill = lineBrush,
        Opacity = 0.08
      };
      FanCurveCanvas.Children.Add(fill);

      // Draw control points
      for (int i = 0; i < sorted.Count; i++) {
        double x = padL + (sorted[i].temp - MinTemp) / (MaxTemp - MinTemp) * chartW;
        double y = padT + chartH - (sorted[i].rpm / MaxRPM) * chartH;

        var circle = new Ellipse {
          Width = CurvePointRadius * 2, Height = CurvePointRadius * 2,
          Fill = accentBrush,
          Stroke = lineBrush, StrokeThickness = 1.5,
          Cursor = Cursors.Hand,
          Tag = i
        };
        Canvas.SetLeft(circle, x - CurvePointRadius);
        Canvas.SetTop(circle, y - CurvePointRadius);
        FanCurveCanvas.Children.Add(circle);

        // RPM tooltip label
        var tip = new TextBlock {
          Text = sorted[i].rpm + " RPM",
          FontSize = 9, Foreground = accentBrush,
          Tag = "label"
        };
        Canvas.SetLeft(tip, x - 15);
        Canvas.SetTop(tip, y - 18);
        FanCurveCanvas.Children.Add(tip);
      }

      // Draw current temp indicator
      float cpuTemp = HardwareService.CPUTemp;
      if (cpuTemp >= MinTemp && cpuTemp <= MaxTemp) {
        double tx = padL + (cpuTemp - MinTemp) / (MaxTemp - MinTemp) * chartW;
        var indicatorLine = new Line {
          X1 = tx, Y1 = padT, X2 = tx, Y2 = padT + chartH,
          Stroke = accentBrush, StrokeThickness = 1.5,
          StrokeDashArray = new DoubleCollection { 2, 2 },
          Opacity = 0.7
        };
        FanCurveCanvas.Children.Add(indicatorLine);
        var tempLabel = new TextBlock {
          Text = (int)cpuTemp + "°C",
          FontSize = 10, Foreground = accentBrush, FontWeight = FontWeights.Bold
        };
        Canvas.SetLeft(tempLabel, tx - 12);
        Canvas.SetTop(tempLabel, padT - 2);
        FanCurveCanvas.Children.Add(tempLabel);
      }
    }

    void FanCurveCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
      var pos = e.GetPosition(FanCurveCanvas);
      double w = FanCurveCanvas.ActualWidth;
      double h = FanCurveCanvas.ActualHeight;
      double padL = 5, padR = 5, padT = 10, padB = 20;
      double chartW = w - padL - padR;
      double chartH = h - padT - padB;

      if (_curvePoints == null) return;
      var sorted = _curvePoints.OrderBy(p => p.temp).ToList();

      for (int i = 0; i < sorted.Count; i++) {
        double px = padL + (sorted[i].temp - MinTemp) / (MaxTemp - MinTemp) * chartW;
        double py = padT + chartH - (sorted[i].rpm / MaxRPM) * chartH;
        if (Math.Abs(pos.X - px) < 15 && Math.Abs(pos.Y - py) < 15) {
          _draggingIndex = i;
          FanCurveCanvas.CaptureMouse();
          e.Handled = true;
          return;
        }
      }
    }

    void FanCurveCanvas_MouseMove(object sender, MouseEventArgs e) {
      if (_draggingIndex < 0 || _curvePoints == null) return;

      var pos = e.GetPosition(FanCurveCanvas);
      double w = FanCurveCanvas.ActualWidth;
      double h = FanCurveCanvas.ActualHeight;
      double padL = 5, padR = 5, padT = 10, padB = 20;
      double chartW = w - padL - padR;
      double chartH = h - padT - padB;

      var sorted = _curvePoints.OrderBy(p => p.temp).ToList();

      // Calculate new temp and rpm from mouse position
      float newTemp = (float)((pos.X - padL) / chartW * (MaxTemp - MinTemp) + MinTemp);
      float newRpm = (float)((padT + chartH - pos.Y) / chartH * MaxRPM);

      // Clamp temperature: don't cross neighbors
      float minT = _draggingIndex > 0 ? sorted[_draggingIndex - 1].temp + 1 : MinTemp;
      float maxT = _draggingIndex < sorted.Count - 1 ? sorted[_draggingIndex + 1].temp - 1 : MaxTemp;
      newTemp = Math.Max(minT, Math.Min(maxT, newTemp));

      // Clamp RPM
      newRpm = Math.Max(0, Math.Min(MaxRPM, newRpm));

      // Round to nice values
      newTemp = (float)Math.Round(newTemp);
      newRpm = (float)(Math.Round(newRpm / 100) * 100);

      sorted[_draggingIndex] = ((float)newTemp, (int)newRpm);
      _curvePoints = sorted;

      DrawFanCurve();
    }

    void FanCurveCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
      if (_draggingIndex >= 0) {
        _draggingIndex = -1;
        FanCurveCanvas.ReleaseMouseCapture();
        // Save and apply
        FanService.SaveCustomCurve(_curvePoints);
        ApplyCustomCurve();
      }
    }
  }
}
