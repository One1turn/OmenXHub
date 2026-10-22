// WindowHelper.cs - 弹窗生命周期辅助
// ponytail: WPF owned window 关闭时会对 owner 做「焦点/激活还给 owner」操作;当弹窗与模态
// 弹窗(DialogHelper ShowDialog)焦点链交错,或 owner 处于托盘隐藏等状态时,关闭 owned window
// 可能把主窗口误最小化(实测,issue: 关闭 GPU 程序弹窗后主界面最小化)。
// 统一解法:所有设置 Owner 的弹窗在创建后调用 DetachOwnerOnClose,关闭前断开 Owner,
// 使关闭成为独立窗口关闭,不影响主窗口状态。
using System;
using System.Windows;

namespace OmenSuperHub.Utils {
  internal static class WindowHelper {
    /// <summary>把重活推迟到导航过渡动画跑完之后。
    /// Wpf.Ui 的 NavigationView 默认 Transition=FadeInWithSlide/200ms,过渡方式是对新页做
    /// Opacity 0→1 + RenderTransform.Y 30→0 —— 靠渲染帧推进。若页在 Loaded(或其排队的
    /// DispatcherPriority.Loaded)里同步干重活,UI 线程被占住,动画一帧都渲染不出来,整段上升
    /// 就"丢"了(离屏探针 16ms 采样实测:PerfPage 导航后阻塞 ~575ms 才出帧,出帧时动画仍停在
    /// op=0.00/Y=30 初始态;NetworkBoostPage 同类阻塞 ~597ms)。
    /// ponytail: 240ms = Wpf.Ui NavigationView.TransitionDuration 默认值 200 + 余量。
    /// 若将来显式设置 TransitionDuration,此处需同步改。</summary>
    public static void RunAfterNavTransition(System.Windows.Threading.Dispatcher dispatcher, Action work) {
      if (dispatcher == null || work == null) return;
      var timer = new System.Windows.Threading.DispatcherTimer(
        System.Windows.Threading.DispatcherPriority.Background, dispatcher) {
        Interval = TimeSpan.FromMilliseconds(240)
      };
      timer.Tick += (s, e) => { timer.Stop(); work(); };
      timer.Start();
    }

    public static void DetachOwnerOnClose(Window w) {
      if (w == null) return;
      w.Closing += (s, e) => {
        if (w.Owner == null) return;
        // ponytail: 模态弹窗(ShowDialog)在 Closing 期间 _showingAsDialog 仍为 true,WPF 冻结了
        // Owner(Window.Owner setter 抛 CantSetOwnerAfterDialogIsShown)。此时断开不可能、也不需要 —
        // WPF 自己的 DoDialogHide 会完成 owner 交接。非模态窗口(Show)照旧断开 Win32 owner,保住原修复。
        try { w.Owner = null; }
        catch (System.InvalidOperationException) { }
      };
    }
  }
}
