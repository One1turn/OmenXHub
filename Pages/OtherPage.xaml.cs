// OtherPage.cs - 其他功能开关页面
// 锁定键、触控板、HWiNFO 集成、HTTP API 等杂项开关
using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OmenSuperHub.Services;
using OmenSuperHub.Utils;
using System.Windows.Threading;

namespace OmenSuperHub.Pages
{
    public partial class OtherPage : Page
    {
        bool _loading;

        DispatcherTimer _hwinfoTimer;

        public OtherPage()
        {
            InitializeComponent();
            // ponytail: 合并成一个命名方法 —— 原先两个 `Loaded += lambda` 既退订不了,
            // 也让"初始化 + 启 timer"分散在两处;命名方法便于 Unloaded 对称停止。
            Loaded += OtherPage_Loaded;
            Unloaded += OtherPage_Unloaded;
            _hwinfoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _hwinfoTimer.Tick += OnHwinfoTick;
        }

        void OtherPage_Loaded(object s, RoutedEventArgs e) {
            _loading = true;
            LoadState();
            _loading = false;
            _hwinfoTimer?.Start();
        }

        // ponytail: dispatcher timer 必须在 Unloaded 停止，否则页面被导航离开后
        // timer 仍每 2s 触发 UpdateHWiNFOReadStatus()（注册表读 + WMI 探测），
        // 而 OtherPage 已脱离可视树，调用对分离元素无效且占 UI 线程。
        // 注意: 不能在这里退订 Loaded —— 页面被 CachedPageService 复用时要靠它重新初始化。
        void OtherPage_Unloaded(object s, RoutedEventArgs e) {
            _hwinfoTimer?.Stop();
        }

        void OnHwinfoTick(object s, EventArgs e) {
            // ponytail: 主窗口关闭到托盘后,OtherPage 即使被缓存仍在每 2s 跑一次
            // UpdateHWiNFOReadStatus (注册表读 + WMI 探测);可见性恢复前是纯开销。
            // 与 DashboardPage 同款守卫:只 return 不 Stop —— Stop 之后依赖 Loaded 重启,
            // 页面被缓存复用而 Loaded 未再触发时就会永久停更。
            if (Window.GetWindow(this)?.IsVisible != true) return;
            UpdateHWiNFOReadStatus();
        }

        void LoadState()
        {
            NumLockToggle.IsChecked = false;
            CapsLockToggle.IsChecked = false;
            // ponytail: 触摸板禁用是 DICS_FLAG_GLOBAL,跨重启保持 —— 必须回显真实设备状态,
            // 否则重启后 UI 显示"未锁"而触摸板其实还是禁用的,用户无从恢复。
            TouchpadLockToggle.IsChecked = TouchpadLockService.IsLocked();
            // ponytail: 启动时查 EC 锁状态同步 UI(HP 机型走 WMI 0x2000B),
            // 避免上次锁了重启后 UI 显示关但实际 EC 还锁着。查不到则默认关。
            WinLockToggle.IsChecked = WinKeyLockService.SyncFromEc() ?? false;
            HWiNFOToggle.IsChecked = ConfigService.HWiNFOEnabled;
            HWiNFOReadToggle.IsChecked = ConfigService.HWiNFOReadEnabled;
            UpdateHWiNFOReadStatus();
            HttpApiToggle.IsChecked = ConfigService.HttpApiEnabled;
            LanAccessToggle.IsChecked = ConfigService.LanAccessEnabled;
            UpdateHttpApiStatus();
        }

        void NumLockToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ToggleKey(VK_NUMLOCK);
        }

        void CapsLockToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ToggleKey(VK_CAPITAL);
        }

        void TouchpadLockToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            // ponytail: 走 SetupAPI 禁用/启用设备节点(DICS_FLAG_GLOBAL,跨重启保持)。
            // 设备状态切换是同步 PnP 操作(几十到几百 ms),放后台线程避免卡 UI;
            // 完成后用 IsLocked() 回查真实状态 —— 驱动拒绝时能如实回滚并提示,
            // 而不是像旧版那样静默失败(旧版还因 DIGCF_ALLCLASSES 常量写错从未生效过)。
            bool wantLocked = TouchpadLockToggle.IsChecked == true;
            TouchpadLockToggle.IsEnabled = false;
            Task.Run(() => {
                string err = null;
                int changed = 0;
                try { changed = TouchpadLockService.SetEnabled(!wantLocked); }
                catch (Exception ex) { err = ex.Message; }
                bool actualLocked = false;
                try { actualLocked = TouchpadLockService.IsLocked(); }
                catch { }
                if (err == null && actualLocked != wantLocked)
                    Logger.Warn($"[TouchpadLock] want={wantLocked} actual={actualLocked} changed={changed}");
                Dispatcher.BeginInvoke(new Action(() => {
                    TouchpadLockToggle.IsEnabled = true;
                    if (actualLocked == wantLocked) return;
                    // 回滚 IsChecked 也会触发本 handler,_loading 抑制再入
                    _loading = true;
                    try { TouchpadLockToggle.IsChecked = actualLocked; }
                    finally { _loading = false; }
                    if (err != null) DialogHelper.Warn(Strings.TouchpadLockFailed + "\n" + err);
                    // changed==0 且状态没变 = 没匹配到触摸板节点(名称/类不符),与"驱动拒绝
                    // 改状态"是两回事 —— 分开提示才好定位。
                    else if (changed == 0) DialogHelper.Warn(Strings.TouchpadLockNoDevice);
                    else DialogHelper.Warn(Strings.TouchpadLockFailed);
                }));
            });
        }

        void WinLockToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            WinKeyLockService.SetEnabled(WinLockToggle.IsChecked == true);
        }

        void HWiNFOToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ConfigService.HWiNFOEnabled = HWiNFOToggle.IsChecked == true;
            ConfigService.Save("HWiNFOEnabled");
            HWiNFOService.StartStopIfNeeded();
        }

        void HWiNFOReadToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ConfigService.HWiNFOReadEnabled = HWiNFOReadToggle.IsChecked == true;
            ConfigService.Save("HWiNFOReadEnabled");
            HWiNFOReaderService.StartStopIfNeeded();
            UpdateHWiNFOReadStatus();
        }

        void UpdateHWiNFOReadStatus()
        {
            if (HWiNFOReadStatusText == null) return;
            if (ConfigService.HWiNFOReadEnabled && HWiNFOReaderService.IsRunning)
            {
                HWiNFOReadStatusText.Text = HWiNFOReaderService.StatusText;
                HWiNFOReadStatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;
            }
            else
            {
                HWiNFOReadStatusText.Text = HWiNFOReaderService.StatusText;
                HWiNFOReadStatusText.Foreground = System.Windows.Media.Brushes.Gray;
            }
        }

        void HttpApiToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ConfigService.HttpApiEnabled = HttpApiToggle.IsChecked == true;
            ConfigService.Save("HttpApiEnabled");
            if (ConfigService.HttpApiEnabled)
            {
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    HardwareApiService.Start();
                    Dispatcher.BeginInvoke(new Action(UpdateHttpApiStatus));
                });
            }
            else
            {
                HardwareApiService.Stop();
                UpdateHttpApiStatus();
            }
        }

        void LanAccessToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ConfigService.LanAccessEnabled = LanAccessToggle.IsChecked == true;
            ConfigService.Save("LanAccessEnabled");
            // ponytail: 绑定前缀在 Start 时确定,切换必须重启监听才生效(含防火墙规则增删)。
            if (HardwareApiService.IsRunning)
            {
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    HardwareApiService.Stop();
                    HardwareApiService.Start();
                    Dispatcher.BeginInvoke(new Action(UpdateHttpApiStatus));
                });
            }
        }

        void LanShareButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new OmenSuperHub.Views.LanShareWindow { Owner = Window.GetWindow(this) };
            win.ShowDialog();
        }

        void UpdateHttpApiStatus()
        {
            if (HttpApiStatusText == null) return;
            // ponytail: LAN 开关只在 API 运行时可切换 —— API 关时绑定前缀无意义。
            if (LanAccessToggle != null) LanAccessToggle.IsEnabled = HardwareApiService.IsRunning;
            if (HardwareApiService.IsRunning)
            {
                // ponytail: 展示 Bearer 令牌 —— 客户端唯一的获取入口,选中即可复制
                HttpApiStatusText.Text = Strings.HttpApiRunningWithToken(HardwareApiService.ApiToken);
                HttpApiStatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;
            }
            else
            {
                HttpApiStatusText.Text = Strings.HttpApiStopped;
                HttpApiStatusText.Foreground = System.Windows.Media.Brushes.Gray;
            }
        }

        // 系统优化二级弹窗：Windows 服务 + 启动项
        void SysOptOpen_Click(object sender, RoutedEventArgs e)
        {
            var win = new OmenSuperHub.Views.SystemOptimizeWindow
            {
                Owner = Window.GetWindow(this)
            };
            win.ShowDialog();
        }

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
        const uint KEYEVENTF_KEYUP = 0x02;
        const byte VK_NUMLOCK = 0x90;
        const byte VK_CAPITAL = 0x14;

        static void ToggleKey(byte vk)
        {
            keybd_event(vk, 0, 0, IntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        }

    }
}
