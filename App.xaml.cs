using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

using OmenSuperHub.Services;
using Microsoft.Win32;

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
          "DispatcherException: " + args.Exception.Message + "\n\n" + args.Exception.StackTrace,
          "OmenSuperHub Error", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
      };

      try {
        // Single instance check
        bool isNewInstance;
        _mutex = new Mutex(true, "MyUniqueAppMutex", out isNewInstance);
        if (!isNewInstance) {
          Shutdown();
          return;
        }

        if (Environment.OSVersion.Version.Major >= 6) {
          SetProcessDPIAware();
        }

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // Initialize System Theme integration
        ThemeService.Initialize();

        // Initialize power status
        HardwareService.PowerOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        HardwareService.MonitorQuery();

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
      _mutex?.ReleaseMutex();
      _mutex?.Dispose();
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

