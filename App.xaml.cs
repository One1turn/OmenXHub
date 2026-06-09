using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

using OmenSuperHub.Services;
using Microsoft.Win32;
using static OmenSuperHub.OmenHardware;

namespace OmenSuperHub {
  public partial class App : System.Windows.Application {
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    static Mutex _mutex;
    static int alreadyReadCode = 1000;

    protected override void OnStartup(StartupEventArgs e) {
      base.OnStartup(e);

      // Add dispatcher exception handler for XAML errors
      this.DispatcherUnhandledException += (s, args) => {
        System.Windows.MessageBox.Show(
          "已经打开窗口", "OmenXHub", MessageBoxButton.OK, MessageBoxImage.Information);
        args.Handled = true;
      };

      try {
        // Single instance check
        bool isNewInstance;
        _mutex = new Mutex(true, "MyUniqueAppMutex", out isNewInstance);
        if (!isNewInstance) {
          System.Windows.MessageBox.Show("已经打开窗口", "OmenXHub", MessageBoxButton.OK, MessageBoxImage.Information);
          Shutdown();
          return;
        }

        if (Environment.OSVersion.Version.Major >= 6) {
          SetProcessDPIAware();
        }

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // Initialize Logger
        Logger.Info("OmenXHub starting...");

        // Load language config
        ConfigService.Load();
        if (!string.IsNullOrEmpty(ConfigService.Language)) {
          switch (ConfigService.Language) {
            case "TraditionalChinese": Strings.Current = AppLanguage.TraditionalChinese; break;
            case "English": Strings.Current = AppLanguage.English; break;
            default: Strings.Current = AppLanguage.SimplifiedChinese; break;
          }
        }

        // Preload NvidiaApi.dll for Hot Switch (DDS)
        if (HardwareService.PowerOnline) {
          try { OmenHardware.ExtractAndPreloadNativeDll("NvidiaApi.dll"); } catch { }
        }

        // Initialize System Theme integration
        ThemeService.Initialize();

        // Initialize power status
        HardwareService.PowerOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        HardwareService.MonitorQuery();

        // Initialize HP Performance Control SDK (required for CPU power WMI to work)
        InitPerformanceControl();

        // Set unleash mode — required before CPU power limit takes effect
        try { SetFanMode((byte)0x31); } catch { }

        // Show warning if SDK init failed
        if (!OmenHardware.IsPowerControlSupported) {
          Logger.Error("CPU power control may not work — HP SDK init failed!");
        }

        // Version-based read code
        Version version = Assembly.GetExecutingAssembly().GetName().Version;
        string versionString = version.ToString().Replace(".", "");
        alreadyReadCode = new Random(int.Parse(versionString)).Next(1000, 10000);

        // Initialize tray icon (WinForms NotifyIcon + WPF ContextMenu)
        TrayService.InitTrayIcon();

        // Initialize hardware monitoring
        HardwareService.LibreComputer.Open();

        // Start timers
        TrayService.StartTimers();

        // Start Omen Key listener
        TrayService.GetOmenKeyTask();

        // Restore last settings
        TrayService.RestoreConfig();

        // Show help for new version
        if (ConfigService.AlreadyRead != alreadyReadCode) {
          Views.HelpWindow.ShowInstance();
          ConfigService.AlreadyRead = alreadyReadCode;
          ConfigService.Save("AlreadyRead");
        }

        // Power change handler
        SystemEvents.PowerModeChanged += TrayService.OnPowerChange;
      } catch (Exception ex) {
        System.Windows.MessageBox.Show(
          "Startup Error: " + ex.Message + "\n\n" + ex.ToString(),
          "OmenSuperHub Error", MessageBoxButton.OK, MessageBoxImage.Error);
      }
    }

    protected override void OnExit(ExitEventArgs e) {
      try { _mutex?.ReleaseMutex(); } catch { }
      try { _mutex?.Dispose(); } catch { }
      base.OnExit(e);
    }

    static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) {
      Exception ex = e.ExceptionObject as Exception;
      System.Windows.MessageBox.Show(
        "Unhandled Exception: " + ex?.Message + "\n\n" + ex?.StackTrace,
        "OmenSuperHub Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
  }
}

