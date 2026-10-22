// App.xaml.cs - 应用程序入口
// 互斥锁单实例、Logger 初始化、ConfigService 加载、主题/托盘/HWiNFO/API 启动、窗口管理
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

using OmenSuperHub.Services;
using OmenSuperHub.Services.NetworkBoost;
using OmenSuperHub.Utils;
using Microsoft.Win32;
using static OmenSuperHub.OmenHardware;

namespace OmenSuperHub {
  public partial class App : System.Windows.Application {
    static Mutex _mutex;
    static bool _ownsMutex;  // ponytail: 仅在 createdNew==true 时 ReleaseMutex,否则 SynchronizationLockException
    static int alreadyReadCode = 1000;

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    protected override void OnStartup(StartupEventArgs e) {
      RenderOptions.ProcessRenderMode = RenderMode.Default;
      base.OnStartup(e);

      // ponytail: --intelmsr 只读转储 Intel 调校相关 MSR + CPU 拓扑，用于确认本机 E 核倍率寄存器布局。
      // 不写任何硬件寄存器。用法（管理员）：OmenXHub.exe --intelmsr
      if (e.Args.Length > 0 && e.Args[0] == "--intelmsr") {
        string dump;
        try { dump = OmenSuperHub.Services.XtuService.DumpDiagnostics(); }
        catch (Exception ex) { dump = "DUMP FAILED: " + ex; }
        Console.WriteLine(dump);
        try {
          System.IO.File.WriteAllText(
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "intel_msr_dump.txt"), dump);
        } catch { }
        Shutdown();
        return;
      }

      // ponytail: --selftest 运行新逻辑自检断言后退出，不启动 UI。用法：OmenXHub.exe --selftest
      if (e.Args.Length > 0 && e.Args[0] == "--selftest") {
        string result = OmenSuperHub.Services.CpuAffinity.SelfCheck.Run()
          + "\n" + OmenSuperHub.Services.LightingSceneService.SelfCheck()
          + "\n" + OmenSuperHub.Pages.LightingPage.LightBarAnimsSelfCheck()
          + "\n" + OmenSuperHub.Pages.LightingPage.LayoutStateSelfCheck()
          + "\n" + OmenSuperHub.Pages.PerfPage.GuardSelfCheck()
          + "\n" + OmenSuperHub.Views.MainWindow.WindowPlacementSelfCheck()
          + "\n" + OmenSuperHub.Services.LightingAnimationService.SelfCheck()
          + "\n" + OmenSuperHub.Services.DiskCleaner.SelfCheck()
          + "\n" + OmenSuperHub.Services.HardwareService.SelfCheck()
          + "\n" + OmenSuperHub.Services.HardwareApiService.SelfCheck()
          + "\n" + IntelTuneSelfCheck()
          + "\n" + OmenSuperHub.Services.AutomationProcessor.SelfCheck()
          + "\n" + OmenSuperHub.Services.MacroController.SelfCheck()
          + "\n" + OmenSuperHub.Services.MacroService.SelfCheck()
          + "\n" + OmenSuperHub.Services.RaplPowerLimitService.SelfCheck()
          + "\n" + OmenSuperHub.OmenHardware.SelfCheck()
          + "\n" + OmenSuperHub.Services.NetworkBoost.BoostService.SelfCheck()
          + "\n" + OmenSuperHub.Services.NetworkBoost.SystemProxyManager.SelfCheck()
          + "\n" + OmenSuperHub.Services.NetworkBoost.RateLimiter.SelfCheck()
          + "\n" + OmenSuperHub.Services.FanService.SelfCheck()
          + "\n" + OmenSuperHub.Services.TrayService.SelfCheck()
          + "\n" + OmenSuperHub.Pages.NativeMethods_Display.SelfCheck()
          + "\n" + OmenSuperHub.Services.NetworkBoost.TunManager.SelfCheck()
          + "\n" + OmenSuperHub.Services.ThreadBindingService.SelfCheck()
          + "\n" + OmenSuperHub.Pages.DashboardPage.GpuPrefSelfCheck()
          + "\n" + OmenSuperHub.Pages.DashboardPage.StorageBarSelfCheck()
          + "\n" + OmenSuperHub.Views.OsdWindow.OsdKindsSelfCheck()
          + "\n" + OmenSuperHub.Pages.DashboardPage.PresetComboSyncSelfCheck()
          + "\n" + OmenSuperHub.Pages.FanPage.ChartInterpolateSelfCheck()
          + "\n" + OmenSuperHub.Services.TouchpadLockService.SelfCheck();
        // ponytail: 关面板释放前端内存的自检 —— 跑完静态自检后启动一次主窗 → 导航 Dashboard
        // (热缓存 +订阅 OnPresetCycled) → Hide 触发 IsVisibleChanged→ReleaseFrontend → 反射断言
        // 三条:页面缓存清空 / PerfPage.Instance 断开 / OnPresetCycled 订阅归零。任一不成立写 FAIL。
        result += "\n[FrontendRelease] " + RunFrontendReleaseSelfCheck();
        // ponytail: 二级弹窗骨架自检 —— ExtendsContentIntoTitleBar 抹掉原生标题栏后,
        // 每个弹窗必须自带 ui:TitleBar(拖拽区 + 系统关闭键)。原先 5 个窗口只有手搓标题 Border,
        // 只能靠自绘 X 关闭且完全不能拖动。这里逐个实例化(不 Show,只套模板)断言。
        result += "\n[DialogChrome] " + RunDialogChromeSelfCheck();
        // ponytail: 页面标题排版断言 —— 11 页共用 FontSizePageTitle(24)/Medium。
        // token 键名拼错时 DynamicResource 静默退回继承字号(12),构建不报错,只有这里能抓到。
        result += "\n[PageTitleType] " + RunPageTitleTypeSelfCheck();
        // ponytail: 卡头排版断言 —— 68 张卡的标题统一 14 SemiBold、图标统一 18。
        // 原先同一层级混着 14/13/继承(12) 三种标题字号,跨页翻动标题会跳。
        result += "\n[CardHeaderType] " + RunCardHeaderTypeSelfCheck();
        // ponytail: 空态断言 —— 四处空列表提示共用 OmenEmptyState(次级色/13px/居中/限宽)。
        result += "\n[EmptyState] " + RunEmptyStateSelfCheck();
        // ponytail: 弹窗严重度断言 —— Info/Warn/Error 各自带同色左侧色条,不再只靠图标区分。
        result += "\n[DialogSeverity] " + RunDialogSeveritySelfCheck();
        // ponytail: 滚动条翻页热区断言 —— 两个透明 RepeatButton 必须挂在 Track 的
        // Decrease/Increase 分区里。写成 Grid 兄弟节点会互相完全覆盖,命中永远落在最后一个
        // (实测点轨道顶端也是 PageDown),而这个错误在视觉上完全看不出来。
        result += "\n[ScrollBarPaging] " + RunScrollBarPagingSelfCheck();
        // ponytail: 托盘重建断言 —— explorer 重启广播 TaskbarCreated 后必须重新 NIM_ADD,
        // 否则图标永久消失(旧实现 _added 恒 true,只发 NIM_MODIFY)。
        result += "\n[TrayRecreate] " + RunTrayRecreateSelfCheck();
        Console.WriteLine(result);
        try {
          // WinExe 无控制台时输出不可见，同时落盘方便验证
          System.IO.File.WriteAllText(
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_result.txt"), result);
        } catch { }
        Environment.ExitCode = result.Contains("FAIL") ? 1 : 0;
        Shutdown();
        return;
      }

      // Dispatcher exception handler — log and prevent crash loop
      this.DispatcherUnhandledException += (s, args) => {
        Logger.Error($"Dispatcher exception: {args.Exception}");
        args.Handled = true;
      };

      // R15/BUG-R15-8b: 项目多处 Task.Run/async 火-忘(App.xaml.cs:139、FanPage 除尘等),
      // 未观察异常默认静默 —— 崩溃现场丢失。补日志级 handler(不 Handled,维持默认不崩策略)。
      System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) => {
        Logger.Error($"Unobserved task exception: {args.Exception}");
        args.SetObserved();
      };

      try {
        // Single instance check
        _mutex = new Mutex(true, "MyUniqueAppMutex", out _ownsMutex);
        if (!_ownsMutex) {
          ShowExistingWindow();
          Shutdown();
          return;
        }

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // Initialize Logger
        Logger.Info("OmenXHub starting...");

        // Load language config
        ConfigService.Load();
        CustomPresetNamesStore.Load(); // file fallback for custom preset names
        // ponytail: 持久化 FanSync 首次启动的默认 true。若用户已关掉并保存 (注册表有 FanSync=false),
        // Load 读到 false, Save("FanSync") 把 false 写回, 行为不变。首次安装则 true 落地。
        try { ConfigService.Save("FanSync"); } catch { }
        // Re-apply saved preset so its values populate ConfigService fields before RestoreConfig
        if (!string.IsNullOrEmpty(ConfigService.Preset)) {
          PresetManager.SwitchPreset(ConfigService.Preset);
          // ponytail: SetGpuPowerState removed here — TPP (ConcurrentTDP) must be
          // written BEFORE GPU power state for PPAB to use the right power budget.
          // ApplyPresetHardware in MainWindow.Loaded already does them in the
          // correct order: CPU power → TPP → GPU power state.
        }
        // 灯光场景联动 — 预设切换时自动切换灯光场景 (已实现但未接入的 OnPerformanceModeChanged)
        PresetManager.OnPresetChanged += (preset) => LightingSceneService.NotifyPresetChanged(preset);
        // ponytail: 灯光 60s 定时调度器不再在 App 启动时无条件启动 — 此时 _file 还是 null,
        // 跑也是空 tick。改为在 LightingSceneService.Initialize() 内首次加载完 _file 后
        // 按真值 _file.Enabled 决定是否 StartScheduler。页面总开关 → Enabled setter 也会
        // 双向联动调度器启停 (见 LightingSceneService.cs)。
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

        // Preload OmenLightingSDK.dll for native lighting control
        try { OmenHardware.ExtractAndPreloadNativeDll("OmenLightingSDK.dll"); } catch { }

        // ponytail: persisted lighting state wasn't reapplied on boot — user had to hit
        // "Apply Lighting" manually after every reboot. Replay it 5s after launch (off-UI
        // thread so cold-boot window doesn't block on slow WMI/HID open). PerKey HID path
        // skipped here (device probe may fail at cold boot; user can hit the PerKey card's
        // Apply button to re-establish).
        System.Threading.ThreadPool.QueueUserWorkItem(_ => {
          try {
            System.Threading.Thread.Sleep(5000);
            OmenSuperHub.Pages.LightingPage.ReplaySavedLighting();
          } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReplaySavedLighting: {ex.Message}"); }
        });

        // ponytail: 键盘能力探测 — 后台跑(WMI/SDK 冷启动可达数秒),完成后刷新侧栏。
        // 普通键盘(确认探测)时 NavLighting 被 Collapsed;探测失败保守显示,不误隐藏。
        System.Threading.ThreadPool.QueueUserWorkItem(_ => {
          try {
            OmenLighting.DetectKeyboardCapability();
            Dispatcher.BeginInvoke(new Action(() => Views.MainWindow.UpdateNavigationItems()));
          } catch (Exception ex) { Logger.Error($"KeyboardCapability probe: {ex.Message}"); }
        });

        // ponytail: 初次占用整理 —— LibreComputer.Open(驱动+全部传感器)、原生 SDK 预加载、首帧渲染
        // 完成后堆里遗留大量启动期临时对象。延迟 10s(等 LHM Open + 首轮硬件采样跑完)后做一次
        // 全量 GC + 修剪工作集,任务管理器里的初始占用显著下降。
        // 天花板: 只压"初次"峰值 — 页面导航/持续轮询触碰内存后会回到真实水位。
        System.Threading.ThreadPool.QueueUserWorkItem(_ => {
          try {
            System.Threading.Thread.Sleep(10000);
            GC.Collect(2, GCCollectionMode.Optimized);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Optimized);
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            Logger.Info("Startup memory trim done");
          } catch { }
        });

        // Initialize System Theme integration
        ThemeService.Initialize();

        // Initialize power status
        HardwareService.PowerOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        HardwareService.MonitorQuery();

        // Set unleash mode — required before CPU power limit takes effect
        // ponytail: SetFanModeCompat 按机型 ThermalPolicy 版本映射(V1→L7, V0 黑名单→安全档)
        try {
          Logger.Info($"[ThermalPolicy] Version={OmenHardware.GetThermalPolicyVersion()} SystemID={HP.Omen.Core.Model.Device.Models.DeviceModel.ThisSystemID}");
          SetFanModeCompat(0x31);
        } catch { }

        // Version-based read code
        Version version = Assembly.GetExecutingAssembly().GetName().Version;
        string versionString = version.ToString().Replace(".", "");
        // R15/BUG-R15-10: 未来版本号位数增长会使拼接串超 int 范围,裸 Parse 抛
        // OverflowException 中断启动 try 块 —— TryParse 回退固定种子。
        alreadyReadCode = new Random(int.TryParse(versionString, out int vSeed) ? vSeed : 0).Next(1000, 10000);

        // Initialize tray icon (WinForms NotifyIcon + WPF ContextMenu)
        TrayService.InitTrayIcon();

        // Power change handler
        SystemEvents.PowerModeChanged += TrayService.OnPowerChange;

        // Show main window BEFORE heavy init (skip to tray if --tray flag)
        string[] cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length > 1 && cmdArgs[1] == "--tray") {
          Views.MainWindow.StartTrayOnly();
        } else {
          Views.MainWindow.ShowInstance();
        }

        // Init hardware and timers in background — window already visible
        System.Threading.ThreadPool.QueueUserWorkItem(_ => {
          // ponytail: 工作体分步兜底 —— 线程池上未捕获异常直接终止进程(R15/BUG-22 同款)。
          // LHM Open 会碰驱动/传感器,启动早期窗口被关时 Application.Current 可能为 null,
          // 两处都可能在裸奔状态下抛,故各自 try 且互不阻断(传感器挂了仍要起托盘定时器)。
          try { RaplPowerLimitService.StartupHealIfNeeded(); }
          catch (Exception ex) { Logger.Error($"[Startup] RaplHeal: {ex.Message}"); }
          try { HardwareService.LibreComputer.Open(); }
          catch (Exception ex) { Logger.Error($"[Startup] LibreComputer.Open: {ex.Message}"); }
          // ponytail: 预热性能页机型能力探针(合计 ~400ms 的 WMI)—— 预热后 PerfPage 的
          // 构造函数能纯读缓存地定好卡片可见性,首帧就是正确布局,不会"先空后跳"。
          try { OmenSuperHub.Pages.PerfPage.PrimeCapabilityProbes(); }
          catch (Exception ex) { Logger.Verbose($"[PrimeProbes] {ex.Message}"); }
          try {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
              TrayService.StartTimers();
              TrayService.StartTrayHelperTimers();
            }), System.Windows.Threading.DispatcherPriority.Background);
          } catch (Exception ex) { Logger.Error($"[Startup] StartTimers dispatch: {ex.Message}"); }
        });

        // Start HWiNFO64 integration if enabled
        HWiNFOService.StartStopIfNeeded();

        // Start HWiNFO64 reader if enabled
        HWiNFOReaderService.StartStopIfNeeded();

        // Start local HTTP API server if enabled in settings
        if (ConfigService.HttpApiEnabled) {
          // ponytail: QueueUserWorkItem 会静默吞异常 — HTTP 监听失败必须留下痕迹
          System.Threading.ThreadPool.QueueUserWorkItem(_ => {
            try { HardwareApiService.Start(); }
            catch (Exception ex) { Logger.Error("HardwareApiService.Start: " + ex.Message); }
          });
        }

        // Start Omen Key listener
        TrayService.GetOmenKeyTask();

        // Defer non-critical startup work (RestoreConfig, Automation, Macro)
        Dispatcher.BeginInvoke(new Action(() => {
          TrayService.RestoreConfig();
          AutomationService.Initialize();
          // ponytail: 启动门控走中央入口 ReevaluateBackendNeeded —— 双门控 (总开关 + 简洁模式可见性)
          // 任一不满足后端不 Start(WMI watcher/系统事件订阅/全局热键订阅都不上)。
          // 页面总开关/简洁模式 toggle/白名单 cb 切换时由各自回调再次调用本入口真启停。
          AutomationProcessor.ReevaluateBackendNeeded();
          MacroService.Initialize();
          // ponytail: 启动门控 — MacroEnabled=false 时不装全局键盘钩子 (SetWindowsHookEx
          // WH_KEYBOARD_LL 装上后兜所有按键事件)。MacroController.Start 已加幂等守卫,
          // 已 Start 时再调是 no-op。
          if (ConfigService.MacroEnabled) MacroController.Start();
          Views.OsdWindow.StartLockKeyMonitor();
          ApplyIntelPrefetcherStartup();
        }), System.Windows.Threading.DispatcherPriority.Background);

        // Floating window in separate BeginInvoke so it runs even if RestoreConfig throws
        Dispatcher.BeginInvoke(new Action(() => {
          if (ConfigService.FloatingBar == "on")
            Views.FloatingWindow.ShowInstances();
        }), System.Windows.Threading.DispatcherPriority.Background);

        // Show help for new version
        if (ConfigService.AlreadyRead != alreadyReadCode) {
          Views.HelpWindow.ShowInstance(isFirstRun: true);
          ConfigService.AlreadyRead = alreadyReadCode;
          ConfigService.Save("AlreadyRead");
        }
      } catch (Exception ex) {
        DialogHelper.Error("Startup Error: " + ex.Message + "\n\n" + ex.ToString(),
          "OmenSuperHub Error");
      }
    }

    static void ShowExistingWindow() {
      using (var self = Process.GetCurrentProcess()) {
        // R15/BUG-R15-7: GetProcessesByName 返回的 Process 未 Dispose(句柄泄漏);
        // WaitForInputIdle 对无消息循环的同名进程(更新器/僵尸)按文档抛
        // InvalidOperationException —— 冒泡后第二实例的 Shutdown() 被跳过,残留半初始化进程。
        foreach (var p in Process.GetProcessesByName(self.ProcessName)) {
          using (p) {
            if (p.Id == self.Id) continue;
            try { p.WaitForInputIdle(3000); } catch (Exception ex) { Logger.Verbose($"WaitForInputIdle: {ex.Message}"); }
            p.Refresh();
            IntPtr hWnd = p.MainWindowHandle;
            if (hWnd == IntPtr.Zero)
              hWnd = FindWindowForProcess(p.Id);
            if (hWnd != IntPtr.Zero) {
              PostMessage(hWnd, WM_SHOW_MAIN, IntPtr.Zero, IntPtr.Zero);
              return;
            }
          }
        }
      }
    }

    static IntPtr FindWindowForProcess(int processId) {
      // ponytail: 主窗口隐藏到托盘时 MainWindowHandle 为 0,EnumWindows 按 Z 序取
      // 第一个窗口 — 预设切换刚弹过 OSD 浮窗时拿到的是它,唤醒消息被丢弃(#20)。
      // 先按主窗口标题精确匹配,匹配不到再退回第一个。
      IntPtr byTitle = IntPtr.Zero, fallback = IntPtr.Zero;
      string mainTitle = Strings.WindowTitle;
      EnumWindows((hWnd, lParam) => {
        GetWindowThreadProcessId(hWnd, out int pid);
        if (pid != processId) return true;
        if (fallback == IntPtr.Zero) fallback = hWnd;
        if (byTitle == IntPtr.Zero) {
          var sb = new System.Text.StringBuilder(256);
          if (GetWindowText(hWnd, sb, 256) > 0 && sb.ToString() == mainTitle)
            byTitle = hWnd;
        }
        return byTitle == IntPtr.Zero;  // 找到标题匹配即停止枚举
      }, IntPtr.Zero);
      return byTitle != IntPtr.Zero ? byTitle : fallback;
    }

    internal static readonly uint WM_SHOW_MAIN = RegisterWindowMessage("OmenXHubShowMain");

    const int SW_SHOW = 5;
    const int SW_RESTORE = 9;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool FlashWindow(IntPtr hWnd, bool bInvert);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    protected override void OnExit(ExitEventArgs e) {
      // ponytail: 每个 close 操作都独立 try-catch — 任何一个抛出不能阻断后续关闭。
      // SafeShutdown 顺序与原 OnExit 一致。
      void SafeShutdown(Action a) { try { a(); } catch (Exception ex) { Logger.Error("OnExit step failed: " + ex.Message); } }
      if (PresetManager.IsCustom(ConfigService.Preset)) SafeShutdown(() => PresetManager.SaveCustomPreset(ConfigService.Preset));
      SafeShutdown(BoostService.Stop); // 关闭时自动重置加速（停代理/停 TUN/清路由），规则留在本地 json
      SafeShutdown(MacroController.Stop);
      SafeShutdown(HardwareApiService.Stop);
      SafeShutdown(HWiNFOService.Stop);
      SafeShutdown(HWiNFOReaderService.Stop);
      SafeShutdown(ThemeService.Cleanup);
      SafeShutdown(EcoQosService.Cleanup);
      // ponytail: CoreKeep 现状是惰性启动 (默认 false、进 CoreKeepPage 才 StartAutoApply),
      // 但以前没在 OnExit 统一 Cleanup → 用户开过后再退出程序,ManagementEventWatcher 与
      // Timer 由 GC 兜底。StopAutoApply 已是幂等(null 守卫),退出时没启也 ran-nothing。
      // waitForRelax:true — 进程终止会掐断线程池任务,恢复必须同步走完。
      SafeShutdown(() => OmenSuperHub.Services.CpuAffinity.CoreKeepService.StopAutoApply(true));
      SafeShutdown(AutomationProcessor.Stop);
      SafeShutdown(LightingSceneService.StopScheduler);
      // ponytail: RAPL 直写(0x610)的崩溃自愈走"下次启动 boot-scoped 快照回写";
      // 正常退出只清快照,不回写 —— 保持全工程"设置生效直到重启"的既有契约。
      SafeShutdown(RaplPowerLimitService.ClearSnapshotOnExit);
      SafeShutdown(() => SystemEvents.PowerModeChanged -= TrayService.OnPowerChange);
      SafeShutdown(HardwareService.Close);
      // ponytail: EC 句柄(PawnIO LpcAcpiEc)此前不在关闭清单 — Close 幂等(空守卫),没启过也无副作用。
      SafeShutdown(Services.EcFanService.Close);
      SafeShutdown(() => { if (_ownsMutex) _mutex?.ReleaseMutex(); });
      SafeShutdown(() => _mutex?.Dispose());
      base.OnExit(e);
    }

    static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) {
      Exception ex = e.ExceptionObject as Exception;
      // R15/BUG-R15-8: 本处理器运行在崩溃线程(常为非 UI 线程)。DialogHelper._Show 会遍历
      // Application.Current.Windows 并 new FluentWindow —— 非 UI 线程直接调用自抛
      // InvalidOperationException,原始异常信息反而丢失;且原先完全不落盘。
      // 先 Logger 保底,再 marshal 回 UI 线程弹窗,弹窗自身再兜底。
      try { Logger.Error($"UnhandledException: {ex}"); } catch { }
      try {
        var app = Application.Current;
        if (app?.Dispatcher != null) {
          app.Dispatcher.BeginInvoke(new Action(() => {
            try {
              DialogHelper.Error("Unhandled Exception: " + ex?.Message + "\n\n" + ex?.StackTrace,
                "OmenSuperHub Error");
            } catch { }
          }));
        }
      } catch { }
    }

    // ponytail: 关主面板释放前端内存的自检。直接驱动 Services.CachedPageService + 各页 Unloaded,
    // 不启 UI（避免拖累 HardwareService 等后端）。任意 FAIL 写到 selftest_result.txt 并通过
    // 现有 Environment.ExitCode = result.Contains("FAIL") ? 1 : 0 让退出码反映。
    // 三段对应三个补丁：
    //   Patch 3 — CachedPageService.Clear() 清空 _cache（hasClass field 反射验证）
    //   Patch 1 — PerfPage.Unloaded 把 static Instance 置 null
    //   Patch 2 — DashboardPage.Unloaded 解订阅 ConfigService.OnPresetCycled
    // Intel 调校的纯逻辑自检：不初始化 PawnIO、不读写 MSR，任意机型均可跑。
    static string IntelTuneSelfCheck() {
      try {
        OmenSuperHub.Services.XtuService.SelfCheck(out string message);
        return message;
      } catch (Exception ex) {
        return "FAIL XtuService: " + ex.Message;
      }
    }

    // ponytail: 预取器掩码启动重应用 — BIOS 冷启动会把 0x1A4 恢复为全开默认,
    // 用户设置过非零掩码(禁用某预取器)时须写回。掩码为 0 时直接跳过,零开销。
    static void ApplyIntelPrefetcherStartup() {
      try {
        if (ConfigService.IntelPrefetcherMask == 0) return;
        if (!OmenHardware.HasIntelCpu()) return;
        var svc = new OmenSuperHub.Services.XtuService();
        System.Threading.Tasks.Task.Run(async () => {
          try {
            if (await svc.InitializeAsync() && svc.HasPrefetcher
                && svc.TryGetPrefetcherMask() != ConfigService.IntelPrefetcherMask) {
              bool ok = svc.TrySetPrefetcherMask(ConfigService.IntelPrefetcherMask);
              Logger.Info($"[PrefetcherStartup] 重应用掩码 0x{ConfigService.IntelPrefetcherMask:X} => {ok}");
            }
          } finally { svc.Dispose(); }
        });
      } catch (Exception ex) { Logger.Verbose($"[PrefetcherStartup] {ex.Message}"); }
    }

    /// <summary>二级弹窗骨架断言:每个 FluentWindow 弹窗必须恰好一个 ui:TitleBar、可见的关闭键、
    /// 非零高度,且自定义 Header 不可命中(否则 Wpf.Ui 的 WM_NCHITTEST 不发 HTCAPTION,整条栏拖不动)。
    /// ponytail: 不 Show —— ctor 里的 Loaded 逻辑会摸注册表/起后台线程,这里要验的只是骨架;
    /// 内容摘出来挂普通 Border 上 Measure 一遍就能把 ControlTemplate 套出来。</summary>
    static string RunDialogChromeSelfCheck() {
      var fails = new System.Collections.Generic.List<string>();
      var cases = new[] {
        new { Name = "StorageCleanWindow", Args = new object[0], Type = typeof(Views.StorageCleanWindow) },
        new { Name = "SystemOptimizeWindow", Args = new object[0], Type = typeof(Views.SystemOptimizeWindow) },
        new { Name = "ProcessSelectDialog", Args = new object[] { null }, Type = typeof(Views.ProcessSelectDialog) },
        new { Name = "OsdKindsWindow", Args = new object[0], Type = typeof(Views.OsdKindsWindow) },
        new { Name = "LanShareWindow", Args = new object[0], Type = typeof(Views.LanShareWindow) },
        new { Name = "HelpWindow", Args = new object[0], Type = typeof(Views.HelpWindow) },
        new { Name = "PipelineEditorWindow", Args = new object[] { null, null, false }, Type = typeof(Views.PipelineEditorWindow) },
      };
      foreach (var c in cases) {
        System.Windows.Window w = null;
        try {
          w = (System.Windows.Window)Activator.CreateInstance(c.Type, c.Args);
          var content = (System.Windows.UIElement)w.Content;
          w.Content = null;
          var host = new System.Windows.Controls.Border { Child = content, Width = 1200, Height = 900 };
          host.Measure(new System.Windows.Size(1200, 900));
          host.Arrange(new System.Windows.Rect(0, 0, 1200, 900));
          host.UpdateLayout();

          var bars = new System.Collections.Generic.List<System.Windows.DependencyObject>();
          CollectByTypeName(host, "TitleBar", bars);
          if (bars.Count != 1) { fails.Add($"{c.Name}: TitleBar count={bars.Count}, expected 1"); continue; }

          int close = 0;
          bool badForeground = false;
          foreach (var d in DescendantsOf(bars[0])) {
            if (d.GetType().Name != "TitleBarButton") continue;
            var fe = d as System.Windows.FrameworkElement;
            if (fe == null || fe.Visibility != System.Windows.Visibility.Visible) continue;
            var bt = d.GetType().GetProperty("ButtonType");
            string kind = bt == null ? "" : bt.GetValue(d, null).ToString();
            if (kind == "Close") close++;
            // 关闭键的 X 是 Path,填充来自 RenderButtonsForeground;为空时按钮存在但看不见
            var fg = d.GetType().GetProperty("RenderButtonsForeground")?.GetValue(d, null) as System.Windows.Media.Brush;
            if (fg == null) badForeground = true;
          }
          if (close != 1) fails.Add($"{c.Name}: visible Close buttons={close}, expected 1");
          if (badForeground) fails.Add($"{c.Name}: caption button foreground is null (invisible X)");
          if (((System.Windows.FrameworkElement)bars[0]).ActualHeight <= 0) fails.Add($"{c.Name}: TitleBar height=0 (no drag area)");

          var header = bars[0].GetType().GetProperty("Header")?.GetValue(bars[0], null) as System.Windows.UIElement;
          var titleBlock = bars[0].GetType()
            .GetField("_titleBlock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(bars[0]);
          if (header != null && !ReferenceEquals(header, titleBlock) && header.IsHitTestVisible)
            fails.Add($"{c.Name}: TitleBar.Header is hit-testable — title bar cannot be dragged");

          // 标题栏左边界必须和弹窗内容区的 16px 边距对齐。Wpf.Ui 的 TitleBar 模板会在 Header
          // 左边塞一个 TitleGrid(装 Icon),它的 Margin 取 Padding,于是 Header.x = Padding.L+R。
          // 默认 16,4,16,4 得到 x=32(比正文右缩 16);Title/Icon 都为空时 TitleGrid 折叠,
          // Padding 完全失效、Header 掉到 x=0(比正文左伸 16)。两种都算错。
          var hdrFe = header as System.Windows.FrameworkElement;
          if (hdrFe != null && hdrFe.Visibility == System.Windows.Visibility.Visible) {
            var pt = hdrFe.TransformToAncestor(host).Transform(new System.Windows.Point(0, 0));
            if (Math.Abs(pt.X - 16) > 0.6)
              fails.Add($"{c.Name}: TitleBar.Header.x={pt.X:0.0}, expected 16 (aligns with content margin)");
          }
        } catch (System.Exception ex) {
          fails.Add($"{c.Name}: threw {(ex.InnerException ?? ex).GetType().Name}: {(ex.InnerException ?? ex).Message}");
        } finally {
          try { w?.Close(); } catch { }
        }
      }
      return fails.Count == 0 ? "PASS (" + cases.Length + " dialogs)" : "FAIL: " + string.Join(" | ", fails);
    }

    /// <summary>页面标题排版断言:11 页共用 FontSizePageTitle(24) + Medium。
    /// ponytail: 只实例化 SettingsPage 一页 —— 它是 11 处里唯一 ctor 纯 InitializeComponent 的,
    /// 其余页 ctor 有 WMI/配置副作用;这 11 处是同一批机械替换,验 token + 一处实际解析足够。
    /// 真正会静默坏掉的是 token 键名拼错:DynamicResource 找不到时退回继承字号(12),构建不报错。</summary>
    static string RunPageTitleTypeSelfCheck() {
      var fails = new System.Collections.Generic.List<string>();
      object token = Application.Current.TryFindResource("FontSizePageTitle");
      if (!(token is double size) || Math.Abs(size - 24) > 0.01)
        fails.Add($"FontSizePageTitle = {(token ?? "null")}, expected 24");
      try {
        var page = new Pages.SettingsPage();
        var heading = page.FindName("HeadingSettings") as System.Windows.Controls.TextBlock;
        if (heading == null) fails.Add("HeadingSettings not found");
        else {
          if (Math.Abs(heading.FontSize - 24) > 0.01)
            fails.Add($"HeadingSettings.FontSize = {heading.FontSize}, expected 24 (token unresolved?)");
          if (heading.FontWeight != System.Windows.FontWeights.Medium)
            fails.Add($"HeadingSettings.FontWeight = {heading.FontWeight}, expected Medium");
        }
      } catch (System.Exception ex) { fails.Add($"SettingsPage threw {ex.GetType().Name}: {ex.Message}"); }
      return fails.Count == 0 ? "PASS (11 pages, 1 sampled)" : "FAIL: " + string.Join(" | ", fails);
    }

    /// <summary>卡头排版断言:每张卡的标题 = FontSizeTitle(14) SemiBold,图标 = FontSizeHeader(18)。
    /// 改造前同一层级有 14/13/继承(12) 三种标题字号和 18/16/14 三种图标字号,跨页翻动标题会跳。
    /// ponytail: 只量 SettingsPage(18 张卡,ctor 纯 InitializeComponent)——
    /// 这是全站统一的机械替换,验一页足够;真要坏是某页新加的卡又手写了一个字号。</summary>
    static string RunCardHeaderTypeSelfCheck() {
      var fails = new System.Collections.Generic.List<string>();
      var pages = new System.Windows.Controls.Page[] { new Pages.SettingsPage(), new Pages.OtherPage() };
      foreach (var page in pages) {
        var content = (System.Windows.UIElement)page.Content;
        page.Content = null;
        var host = new System.Windows.Controls.Border { Child = content, Width = 1100 };
        host.Measure(new System.Windows.Size(1100, 4000));
        host.Arrange(new System.Windows.Rect(0, 0, 1100, 3000));
        host.UpdateLayout();

        int cards = 0;
        foreach (var d in DescendantsOf(host)) {
          string tn = d.GetType().Name;
          if (tn != "CardControl" && tn != "CardExpander") continue;
          cards++;
          var header = d.GetType().GetProperty("Header")?.GetValue(d, null) as System.Windows.DependencyObject;
          if (header == null) continue;

          System.Windows.Controls.TextBlock title = null;
          System.Windows.FrameworkElement icon = null;
          foreach (var h in DescendantsOf(header)) {
            if (icon == null && h.GetType().Name == "SymbolIcon") icon = h as System.Windows.FrameworkElement;
            var tb = h as System.Windows.Controls.TextBlock;
            if (title == null && tb != null && tb.FontWeight.ToOpenTypeWeight() >= 600
                && !string.IsNullOrWhiteSpace(tb.Text)) title = tb;
          }
          string where = page.GetType().Name + " #" + cards + " \"" + (title?.Text ?? "?") + "\"";
          if (title != null && Math.Abs(title.FontSize - 14) > 0.01)
            fails.Add(where + " title FontSize=" + title.FontSize + ", expected 14");
          // 卡头标题不得自带 Foreground:AccentOmen 与 TextPrimary 同色(写上去零强调),
          // 而且显式色会压掉模板 HeaderContentPresenter 的禁用态变灰。标题一律继承。
          if (title != null && title.ReadLocalValue(System.Windows.Controls.TextBlock.ForegroundProperty)
                != System.Windows.DependencyProperty.UnsetValue)
            fails.Add(where + " title has explicit Foreground (should inherit)");
          if (icon != null) {
            var fs = (double)icon.GetType().GetProperty("FontSize").GetValue(icon, null);
            if (Math.Abs(fs - 18) > 0.01) fails.Add(where + " icon FontSize=" + fs + ", expected 18");
          }
        }
        if (cards == 0) fails.Add(page.GetType().Name + ": no cards found (visual tree not built?)");
      }
      return fails.Count == 0 ? "PASS (2 pages)" : "FAIL: " + string.Join(" | ", fails);
    }

    /// <summary>空态断言:四处「列表为空」的提示必须共用 OmenEmptyState,且文案非空。
    /// 改造前三处各写一套字号/颜色/宽度(12 Medium / 13 / 13+MaxWidth),口径也不一致。</summary>
    static string RunEmptyStateSelfCheck() {
      var fails = new System.Collections.Generic.List<string>();
      var style = Application.Current.TryFindResource("OmenEmptyState") as System.Windows.Style;
      if (style == null) { fails.Add("OmenEmptyState style missing"); return "FAIL: " + string.Join(" | ", fails); }

      var probes = new System.Collections.Generic.List<System.Windows.FrameworkElement>();
      try {
        var sysopt = new Views.SystemOptimizeWindow();
        probes.Add((System.Windows.FrameworkElement)sysopt.FindName("ServiceEmptyHint"));
        probes.Add((System.Windows.FrameworkElement)sysopt.FindName("StartupEmptyHint"));
        sysopt.Close();
      } catch (System.Exception ex) { fails.Add("SystemOptimizeWindow threw " + ex.GetType().Name + ": " + ex.Message); }
      try {
        var psd = new Views.ProcessSelectDialog(null);
        probes.Add((System.Windows.FrameworkElement)psd.FindName("EmptyHint"));
        psd.Close();
      } catch (System.Exception ex) { fails.Add("ProcessSelectDialog threw " + ex.GetType().Name + ": " + ex.Message); }
      try {
        var auto = new Pages.AutomationPage();
        probes.Add((System.Windows.FrameworkElement)auto.FindName("AutoNoPipelinesLabel"));
      } catch (System.Exception ex) { fails.Add("AutomationPage threw " + ex.GetType().Name + ": " + ex.Message); }

      foreach (var fe in probes) {
        if (fe == null) { fails.Add("empty-state element not found"); continue; }
        var tb = fe as System.Windows.Controls.TextBlock;
        if (tb == null) { fails.Add(fe.Name + ": not a TextBlock"); continue; }
        if (!ReferenceEquals(tb.Style, style))
          fails.Add(fe.Name + ": does not use OmenEmptyState");
      }
      return fails.Count == 0 ? "PASS (" + probes.Count + " empty states)" : "FAIL: " + string.Join(" | ", fails);
    }

    /// <summary>弹窗严重度断言:Info/Warn/Error 三种提示必须在正文卡片左侧带一条严重度色条,
    /// 且色条颜色与标题图标一致。改造前三者只有图标颜色不同,卡片底色/边框/标题全一样。</summary>
    static string RunDialogSeveritySelfCheck() {
      var fails = new System.Collections.Generic.List<string>();
      var cases = new[] {
        new { Name = "Info",  Icon = Wpf.Ui.Controls.SymbolRegular.Info24,          BrushKey = "AccentBlueBrush" },
        new { Name = "Warn",  Icon = Wpf.Ui.Controls.SymbolRegular.Warning24,       BrushKey = "AccentYellowBrush" },
        new { Name = "Error", Icon = Wpf.Ui.Controls.SymbolRegular.DismissCircle24, BrushKey = "AccentRedBrush" },
      };
      foreach (var c in cases) {
        try {
          var root = Utils.DialogHelper.BuildDialogBody(
            "selftest", c.Name, c.Icon, c.BrushKey, "#FF000000", false, false, null, null, new int[1]);

          // ponytail: 不 Show 窗口,模板没套上,可视树里只有根 StackPanel —— 按结构直取,
          // 不走 DescendantsOf(root)。root.Children = [标题栏 Border, 正文卡片 Border]。
          var titleHost = root.Children[0] as System.Windows.Controls.Border;
          var titleBar = titleHost == null ? null : titleHost.Child as Wpf.Ui.Controls.TitleBar;
          var header = titleBar == null ? null : titleBar.Header as System.Windows.Controls.StackPanel;
          var iconEl = header == null ? null : header.Children[0] as Wpf.Ui.Controls.SymbolIcon;
          System.Windows.Media.Brush iconBrush = iconEl == null ? null : iconEl.Foreground;

          var contentCard = root.Children.Count > 1 ? root.Children[1] as System.Windows.Controls.Border : null;
          var cardGrid = contentCard == null ? null : contentCard.Child as System.Windows.Controls.Grid;
          var bar = cardGrid == null ? null : cardGrid.Children[0] as System.Windows.Controls.Border;
          System.Windows.Media.Brush barBrush = bar == null ? null : bar.Background;
          if (iconBrush == null) fails.Add(c.Name + ": icon brush null");
          if (barBrush == null) fails.Add(c.Name + ": severity bar missing");
          if (iconBrush != null && barBrush != null && !ReferenceEquals(iconBrush, barBrush))
            fails.Add(c.Name + ": bar color != icon color");
          var expect = Application.Current.TryFindResource(c.BrushKey) as System.Windows.Media.Brush;
          if (expect != null && barBrush != null && !ReferenceEquals(expect, barBrush))
            fails.Add(c.Name + ": bar color != " + c.BrushKey);
        } catch (System.Exception ex) {
          fails.Add(c.Name + " threw " + ex.GetType().Name + ": " + ex.Message);
        }
      }
      return fails.Count == 0 ? "PASS (3 severities)" : "FAIL: " + string.Join(" | ", fails);
    }

    /// <summary>滚动条翻页断言:OmenScrollBar 模板里的两个透明 RepeatButton 必须是 Track 的分区按钮,
    /// 且上方那个绑定 PageUp。写成 Grid 兄弟节点时两者完全重合,命中只会落在最后一个。</summary>
    static string RunScrollBarPagingSelfCheck() {
      var fails = new System.Collections.Generic.List<string>();
      var style = Application.Current.TryFindResource("OmenScrollBar") as System.Windows.Style;
      if (style == null) return "FAIL: OmenScrollBar style missing";
      var sb = new System.Windows.Controls.Primitives.ScrollBar { Orientation = System.Windows.Controls.Orientation.Vertical };
      sb.Style = style;
      var host = new System.Windows.Controls.Border { Child = sb, Width = 12, Height = 400 };
      host.Measure(new System.Windows.Size(12, 400));
      host.Arrange(new System.Windows.Rect(0, 0, 12, 400));
      host.UpdateLayout();

      System.Windows.Controls.Primitives.Track track = null;
      foreach (var d in DescendantsOf(sb)) {
        track = d as System.Windows.Controls.Primitives.Track;
        if (track != null) break;
      }
      if (track == null) { fails.Add("PART_Track missing"); return "FAIL: " + string.Join(" | ", fails); }

      var up = track.DecreaseRepeatButton as System.Windows.Controls.Primitives.RepeatButton;
      var down = track.IncreaseRepeatButton as System.Windows.Controls.Primitives.RepeatButton;
      if (up == null) fails.Add("DecreaseRepeatButton missing (track top not clickable)");
      if (down == null) fails.Add("IncreaseRepeatButton missing (track bottom not clickable)");
      if (up != null && !ReferenceEquals(up.Command, System.Windows.Controls.Primitives.ScrollBar.PageUpCommand))
        fails.Add("DecreaseRepeatButton command != PageUp");
      if (down != null && !ReferenceEquals(down.Command, System.Windows.Controls.Primitives.ScrollBar.PageDownCommand))
        fails.Add("IncreaseRepeatButton command != PageDown");

      int loose = 0;
      foreach (var d in DescendantsOf(sb)) {
        var rb = d as System.Windows.Controls.Primitives.RepeatButton;
        if (rb == null) continue;
        if (ReferenceEquals(rb, up) || ReferenceEquals(rb, down)) continue;
        loose++;
      }
      if (loose > 0) fails.Add(loose + " loose RepeatButton(s) outside Track (they would overlap)");
      return fails.Count == 0 ? "PASS (PageUp/PageDown on track halves)" : "FAIL: " + string.Join(" | ", fails);
    }

    /// <summary>托盘图标重建断言:explorer.exe 重启会广播 TaskbarCreated,收到后必须重新 NIM_ADD。
    /// 改造前没有监听该广播,_added 恒为 true,Update() 永远只发 NIM_MODIFY(对已清空的图标无效),
    /// 图标永久消失、只能重启程序。这里真的把消息投递给 NativeTrayIcon 的窗口过程,断言
    /// (a) 消息号非 0 (b) 计数 +1 (c) 重建后仍处于已注册状态。</summary>
    static string RunTrayRecreateSelfCheck() {
      var fails = new System.Collections.Generic.List<string>();
      if (Utils.NativeTrayIcon.TaskbarCreatedMessage == 0) {
        fails.Add("RegisterWindowMessage(\"TaskbarCreated\") returned 0");
        return "FAIL: " + string.Join(" | ", fails);
      }
      Utils.NativeTrayIcon tray = null;
      try {
        tray = new Utils.NativeTrayIcon();
        tray.SetIcon(System.Drawing.SystemIcons.Application);
        tray.SetTip("selftest");
        tray.Show();
        if (!tray.IsRegistered) { fails.Add("icon not registered before broadcast"); }
        int before = tray.TaskbarRecreateCount;

        // 真投递:SendMessage 走窗口过程,与 explorer 广播同一条路径
        var hwnd = (System.IntPtr)typeof(Utils.NativeTrayIcon)
          .GetProperty("Handle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
          .GetValue(tray, null);
        if (hwnd == System.IntPtr.Zero) fails.Add("tray window handle is zero");
        else {
          SendMessage(hwnd, Utils.NativeTrayIcon.TaskbarCreatedMessage, System.IntPtr.Zero, System.IntPtr.Zero);
          if (tray.TaskbarRecreateCount != before + 1)
            fails.Add("TaskbarCreated not handled (count " + before + " -> " + tray.TaskbarRecreateCount + ")");
          if (!tray.IsRegistered) fails.Add("icon not re-registered after broadcast");
        }
      } catch (System.Exception ex) {
        fails.Add("threw " + ex.GetType().Name + ": " + ex.Message);
      } finally {
        try { tray?.Dispose(); } catch { }
      }
      return fails.Count == 0 ? "PASS (TaskbarCreated re-adds icon)" : "FAIL: " + string.Join(" | ", fails);
    }

    static void CollectByTypeName(System.Windows.DependencyObject root, string typeName,
                                  System.Collections.Generic.List<System.Windows.DependencyObject> sink) {
      if (root.GetType().Name == typeName) sink.Add(root);
      foreach (var d in DescendantsOf(root)) if (d.GetType().Name == typeName) sink.Add(d);
    }

    static System.Collections.Generic.IEnumerable<System.Windows.DependencyObject> DescendantsOf(System.Windows.DependencyObject root) {
      int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
      for (int i = 0; i < n; i++) {
        var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
        yield return c;
        foreach (var d in DescendantsOf(c)) yield return d;
      }
    }

    static string RunFrontendReleaseSelfCheck() {
      var fails = new System.Collections.Generic.List<string>();
      var Npub = System.Reflection.BindingFlags.NonPublic;
      var Inst = System.Reflection.BindingFlags.Instance;
      var Stat = System.Reflection.BindingFlags.Static;

      // ── Patch 3: CachedPageService.Clear() ──────────────────────────────
      try {
        var svc = new Services.CachedPageService();
        var cacheField = typeof(Services.CachedPageService).GetField("_cache", Npub | Inst);
        if (cacheField == null) fails.Add("CachedPageService._cache field missing");
        else {
          var cache = (System.Collections.IDictionary)cacheField.GetValue(svc);
          svc.GetPage(typeof(Pages.PerfPage));
          if (cache.Count != 1) fails.Add($"cache.Count after GetPage(Perf) = {cache.Count}, expected 1");
          svc.Clear();
          if (cache.Count != 0) fails.Add($"cache.Count after Clear = {cache.Count}, expected 0");
        }
      } catch (System.Exception ex) { fails.Add($"Patch3 threw: {ex.GetType().Name}: {ex.Message}"); }

      // ── Patch 1: PerfPage.Unloaded → Instance = null ────────────────────
      try {
        var p = new Pages.PerfPage();
        if (Pages.PerfPage.Instance == null) fails.Add("PerfPage.Instance null right after ctor — Patch 1 untestable");
        else {
          var unloaded = typeof(Pages.PerfPage).GetMethod("PerfPage_Unloaded", Npub | Inst);
          unloaded?.Invoke(p, new object[] { p, new System.Windows.RoutedEventArgs() });
          if (Pages.PerfPage.Instance != null)
            fails.Add($"PerfPage.Instance still set after Unloaded (Patch 1 broken)");
        }
      } catch (System.Exception ex) { fails.Add($"Patch1 threw: {ex.GetType().Name}: {ex.Message}"); }

      // ── Patch 2: DashboardPage.Unloaded detaches OnPresetCycled ─────────
      try {
        var evtField = typeof(ConfigService).GetField("OnPresetCycled", Npub | Stat);
        if (evtField == null) fails.Add("ConfigService.OnPresetCycled backing field missing");
        else {
          var dashboard = new Pages.DashboardPage();
          var onPresetMethod = typeof(Pages.DashboardPage).GetMethod("OnPresetCycled", Npub | Inst);
          var handlerField = typeof(Pages.DashboardPage).GetField("_presetCycledHandler", Npub | Inst);

          // 镜像 Loaded 行为:把 OnPresetCycled 方法 + _presetCycledHandler 字段订阅上去
          var current = (System.Action<string>)evtField.GetValue(null);
          if (onPresetMethod != null)
            current += (System.Action<string>)System.Delegate.CreateDelegate(typeof(System.Action<string>), dashboard, onPresetMethod);
          if (handlerField != null)
            current += (System.Action<string>)handlerField.GetValue(dashboard);
          evtField.SetValue(null, current);

          System.Delegate beforeDel = (System.Delegate)evtField.GetValue(null);
          int subsBefore = beforeDel?.GetInvocationList().Length ?? 0;
          if (subsBefore < 2) fails.Add($"OnPresetCycled subs before Unloaded = {subsBefore}, expected ≥2");

          // 触发真实的 Unloaded 路由事件 — Page 通过 `Unloaded += lambda` 注册的处理器由
          // RaiseEvent 调度执行,与 Cache 清空时的真实路径完全一致。
          dashboard.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.UnloadedEvent, dashboard));

          System.Delegate afterDel = (System.Delegate)evtField.GetValue(null);
          if (afterDel != null) {
            int leftover = 0;
            foreach (System.Delegate d in afterDel.GetInvocationList())
              if (d.Target == dashboard) leftover++;
            if (leftover != 0) fails.Add($"DashboardPage still has {leftover} OnPresetCycled subscription(s) after Unloaded (Patch 2 broken)");
          }
        }
      } catch (System.Exception ex) { fails.Add($"Patch2 threw: {ex.GetType().Name}: {ex.Message}"); }

      // ── FanService 分享码: 合法往返 + 畸形拒绝(剪贴板信任边界,与 JSON 导入同款校验) ──
      try {
        var curve = new System.Collections.Generic.List<(float, int)> { (40f, 1500), (60f, 2200), (80f, 3400) };
        var ok = Services.FanService.ParseShareCode(Services.FanService.GenerateShareCode(curve, "selftest"));
        if (ok == null || ok.Value.points.Count != 3) fails.Add("share code round-trip failed");
        var dup = Services.FanService.ParseShareCode(Services.FanService.GenerateShareCode(
          new System.Collections.Generic.List<(float, int)> { (40f, 1500), (40f, 2200) }, "dup"));
        if (dup != null) fails.Add("share code with duplicate temps accepted");
        var neg = Services.FanService.ParseShareCode(Services.FanService.GenerateShareCode(
          new System.Collections.Generic.List<(float, int)> { (40f, -500), (60f, 2200) }, "neg"));
        if (neg != null) fails.Add("share code with negative rpm accepted");
      } catch (System.Exception ex) { fails.Add($"ShareCode threw: {ex.GetType().Name}: {ex.Message}"); }

      // ── 预取器卡可见后重排无同格叠加(异步显示回归:可见卡序列变化必须重排) ──
      try {
        var page2 = new Pages.PerfPage();
        var pfCard = (System.Windows.FrameworkElement)typeof(Pages.PerfPage)
          .GetField("PrefetcherCard", Npub | Inst).GetValue(page2);
        pfCard.Visibility = System.Windows.Visibility.Visible;
        var cpuGrid = (System.Windows.Controls.Grid)typeof(Pages.PerfPage)
          .GetField("CpuPerfGrid", Npub | Inst).GetValue(page2);
        typeof(Pages.PerfPage).GetMethod("LayoutPerfGrid", Npub | Inst)
          .Invoke(page2, new object[] { cpuGrid, true });
        var cells = new System.Collections.Generic.HashSet<string>();
        foreach (var child in cpuGrid.Children) {
          var fe = child as System.Windows.FrameworkElement;
          if (fe == null || fe.Visibility != System.Windows.Visibility.Visible) continue;
          string cell = System.Windows.Controls.Grid.GetRow(fe) + "," + System.Windows.Controls.Grid.GetColumn(fe);
          if (!cells.Add(cell)) fails.Add("预取器卡可见后重排仍有同格叠加: " + cell);
        }
      } catch (System.Exception ex) { fails.Add($"PrefetcherLayout threw: {ex.GetType().Name}: {ex.Message}"); }


      return fails.Count == 0 ? "OK" : "FAIL: " + string.Join(" | ", fails);
    }
  }
}

