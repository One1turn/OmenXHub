// Harness.cs — Owner 异常修复的确定性验收程序。
// 直接编译真实的 Utils/WindowHelper.cs,在真实 WPF 运行时上断言其契约:
//   1) 关闭模态对话框(ShowDialog)不得让 Owner 赋值抛 InvalidOperationException;
//   2) 关闭弹窗不得使 Owner 窗口不可见或被最小化(回归防护,原 bug 的动机);
//   3) 非模态窗口(Show)路径同样安全。
// 退出码 0 = PASS;1 = FAIL(捕获到异常或回归)。
using System;
using System.Windows;
using System.Windows.Threading;
using OmenSuperHub.Utils;

internal static class Harness {
  static Exception _captured;

  [STAThread]
  static int Main() {
    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    app.DispatcherUnhandledException += (s, e) => { _captured = e.Exception; e.Handled = true; };

    var owner = new Window { Width = 200, Height = 120, Title = "owner" };
    owner.Show();

    // 测试 1:模态对话框(ShowDialog)—— 当前会抛异常的那条路径。
    var dlg = new Window { Width = 140, Height = 100, Title = "modal", Owner = owner };
    WindowHelper.DetachOwnerOnClose(dlg);

    var closer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
    closer.Tick += (s, e) => { closer.Stop(); try { dlg.Close(); } catch (Exception ex) { _captured = ex; } };
    closer.Start();

    // 看门狗:若关闭被异常中断导致对话框卡住,3 秒后强制结束消息泵。
    var watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
    watchdog.Tick += (s, e) => { watchdog.Stop(); app.Shutdown(); };
    watchdog.Start();

    try { dlg.ShowDialog(); } catch (Exception ex) { if (_captured == null) _captured = ex; }

    if (_captured != null) {
      Console.WriteLine("RESULT=FAIL " + _captured.GetType().Name + ": " + _captured.Message);
      return 1;
    }

    // 回归防护:关闭弹窗不得影响 owner 窗口状态(原 bug 动机:"关闭弹窗后主界面被最小化")。
    if (!owner.IsVisible) {
      Console.WriteLine("RESULT=FAIL owner 窗口在弹窗关闭后不可见");
      return 1;
    }
    if (owner.WindowState == WindowState.Minimized) {
      Console.WriteLine("RESULT=FAIL owner 窗口在弹窗关闭后被最小化");
      return 1;
    }

    // 测试 2:非模态窗口(Show)—— 回归防护。
    var win2 = new Window { Width = 140, Height = 100, Title = "nonmodal", Owner = owner };
    WindowHelper.DetachOwnerOnClose(win2);
    win2.Show();
    win2.Close();
    if (_captured != null) {
      Console.WriteLine("RESULT=FAIL(nonmodal) " + _captured.GetType().Name + ": " + _captured.Message);
      return 1;
    }

    Console.WriteLine("RESULT=PASS");
    return 0;
  }
}
