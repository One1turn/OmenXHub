using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OmenSuperHub.Services;

namespace OmenSuperHub.Views {
  public partial class FloatingWindow : Window {
    private static FloatingWindow _instance;

    // Win32 constants for click-through window
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public FloatingWindow() {
      InitializeComponent();
      this.SourceInitialized += FloatingWindow_SourceInitialized;
    }

    private void FloatingWindow_SourceInitialized(object sender, EventArgs e) {
      // Make window click-through and not appear in Alt+Tab
      var hwnd = new WindowInteropHelper(this).Handle;
      int extStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
      SetWindowLong(hwnd, GWL_EXSTYLE, extStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    // ═══════════════════════════════════════════════════════
    // Singleton management
    // ═══════════════════════════════════════════════════════
    public static void ShowInstance() {
      Application.Current?.Dispatcher.Invoke(() => {
        if (_instance == null || !_instance.IsLoaded) {
          _instance = new FloatingWindow();
        }
        _instance.ApplyTextSize();
        _instance.Show();
        DoUpdateText();
      });
    }

    public static void CloseInstance() {
      Application.Current?.Dispatcher.Invoke(() => {
        if (_instance != null && _instance.IsLoaded) {
          _instance.Close();
          _instance = null;
        }
      });
    }

    public static void UpdateText() {
      Application.Current?.Dispatcher.Invoke(() => {
        if (_instance != null && _instance.IsLoaded) {
          DoUpdateText();
        }
      });
    }

    protected override void OnClosed(EventArgs e) {
      base.OnClosed(e);
      _instance = null;
    }

    // ═══════════════════════════════════════════════════════
    // Text update
    // ═══════════════════════════════════════════════════════
    private static void DoUpdateText() {
      if (_instance == null) return;

      // CPU
      float cpuTemp = HardwareService.CPUTemp;
      _instance.CpuTempText.Text = $"{cpuTemp:F1}°C";
      _instance.CpuTempText.Foreground = GetTempBrush(cpuTemp);
      _instance.CpuPowerText.Text = $"{HardwareService.CPUPower:F1}W";

      // GPU
      if (HardwareService.MonitorGPU) {
        _instance.GpuRow.Visibility = Visibility.Visible;
        float gpuTemp = HardwareService.GPUTemp;
        _instance.GpuTempText.Text = $"{gpuTemp:F1}°C";
        _instance.GpuTempText.Foreground = GetTempBrush(gpuTemp);
        _instance.GpuPowerText.Text = $"{HardwareService.GPUPower:F1}W";
      } else {
        _instance.GpuRow.Visibility = Visibility.Collapsed;
      }

      // Fan
      if (HardwareService.MonitorFan) {
        _instance.FanRow.Visibility = Visibility.Visible;
        _instance.FanSpeedText.Text = $"{HardwareService.FanSpeedNow[0] * 100}, {HardwareService.FanSpeedNow[1] * 100}";
      } else {
        _instance.FanRow.Visibility = Visibility.Collapsed;
      }

      // Position
      _instance.UpdatePosition();
    }

    private void ApplyTextSize() {
      double fontSize = ConfigService.TextSize * 0.35; // Scale from GDI+ size to WPF
      if (fontSize < 10) fontSize = 10;
      if (fontSize > 28) fontSize = 28;

      CpuLabel.FontSize = fontSize;
      CpuTempText.FontSize = fontSize;
      CpuPowerText.FontSize = fontSize - 1;
      GpuLabel.FontSize = fontSize;
      GpuTempText.FontSize = fontSize;
      GpuPowerText.FontSize = fontSize - 1;
      FanLabel.FontSize = fontSize;
      FanSpeedText.FontSize = fontSize - 1;
    }

    private void UpdatePosition() {
      if (ConfigService.FloatingBarLoc == "right") {
        this.Left = SystemParameters.PrimaryScreenWidth - this.ActualWidth - 10;
      } else {
        this.Left = 10;
      }
      this.Top = 10;
    }

    // ═══════════════════════════════════════════════════════
    // Temperature color gradient
    // ═══════════════════════════════════════════════════════
    private static SolidColorBrush GetTempBrush(float temp) {
      if (temp < 50) {
        return new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0x53)); // Green
      } else if (temp < 70) {
        // Green → Yellow gradient
        double ratio = (temp - 50) / 20.0;
        byte r = (byte)(0x00 + ratio * 0xFF);
        byte g = (byte)(0xC8 + ratio * (0xC1 - 0xC8));
        byte b = (byte)(0x53 - ratio * 0x53);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
      } else if (temp < 85) {
        // Yellow → Red gradient
        double ratio = (temp - 70) / 15.0;
        byte r = (byte)0xFF;
        byte g = (byte)(0xC1 - ratio * 0xC1);
        byte b = (byte)(0x07 - ratio * 0x07);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
      } else {
        return new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44)); // Red
      }
    }
  }
}
