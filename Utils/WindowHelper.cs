// WindowHelper.cs - 弹窗生命周期辅助
// ponytail: WPF owned window 关闭时会对 owner 做「焦点/激活还给 owner」操作;当弹窗与模态
// 弹窗(DialogHelper ShowDialog)焦点链交错,或 owner 处于托盘隐藏等状态时,关闭 owned window
// 可能把主窗口误最小化(实测,issue: 关闭 GPU 程序弹窗后主界面最小化)。
// 统一解法:所有设置 Owner 的弹窗在创建后调用 DetachOwnerOnClose,关闭前断开 Owner,
// 使关闭成为独立窗口关闭,不影响主窗口状态。
using System.Windows;

namespace OmenSuperHub.Utils {
  internal static class WindowHelper {
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
