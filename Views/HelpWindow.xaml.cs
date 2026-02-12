using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace OmenSuperHub.Views {
  public partial class HelpWindow : Window {
    private static HelpWindow _instance;

    public HelpWindow() {
      InitializeComponent();

      // Version info
      var version = Assembly.GetExecutingAssembly().GetName().Version;
      VersionText.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";

      // Load help content
      LoadContent();
    }

    public static void ShowInstance() {
      Application.Current?.Dispatcher.Invoke(() => {
        if (_instance == null || !_instance.IsLoaded) {
          _instance = new HelpWindow();
        }
        _instance.Show();
        _instance.Activate();
      });
    }

    protected override void OnClosed(EventArgs e) {
      base.OnClosed(e);
      _instance = null;
    }

    private void BtnGitHub_Click(object sender, RoutedEventArgs e) {
      Process.Start("https://github.com/MasonDye/OmenXHub");
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e) {
      Process.Start("https://github.com/MasonDye/OmenXHub/releases");
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) {
      Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri));
      e.Handled = true;
    }

    private void LoadContent() {
      // Update Notes
      UpdateNotesText.Text =
          "更新说明\n\n" +
          "v2.0\n" +
          "- UI 全面重构：全新黑色极简主题\n" +
          "- 新增致谢页面\n" +
          "- 修复已知 Bug\n" +
          "- 优化性能与交互\n\n" +
          "v1.9\n" +
          "- 迁移到 WPF，全新深色主题界面\n" +
          "- 新增分标签页帮助窗口\n" +
          "- 优化浮窗显示效果\n\n" +
          "v1.8\n" +
          "- 新增GPU频率限制功能\n" +
          "- 新增动态系统托盘图标\n" +
          "- 优化自动GPU监控逻辑\n\n" +
          "v1.7\n" +
          "- 新增DB解锁功能\n" +
          "- 新增浮窗显示功能\n" +
          "- 新增Omen键自定义功能\n" +
          "- 优化风扇控制逻辑";

      // Fan Config Help
      FanConfigHelp.Text =
          "风扇配置提供两种模式：\n\n" +
          "【安静模式】以BIOS默认风扇曲线的80%运行，适合日常办公和轻度使用。" +
          "程序会自动从BIOS读取风扇表并生成配置文件(silent.txt)。\n\n" +
          "【降温模式】以BIOS默认风扇曲线的100%运行，适合游戏和高负载场景。" +
          "配置文件为cool.txt。\n\n" +
          "温度灵敏度：控制温度响应的速度。" +
          "\"实时\"会立即响应温度变化；\"高\"（默认）有轻微平滑；\"中\"和\"低\"响应更慢，适合减少风扇转速频繁变化。\n\n" +
          "你也可以手动编辑配置文件（silent.txt / cool.txt）来自定义风扇曲线。" +
          "文件格式：CPU温度,CPU风扇1转速,CPU风扇2转速,GPU温度,GPU风扇1转速,GPU风扇2转速";

      // Fan Control Help
      FanControlHelp.Text =
          "风扇控制选项：\n\n" +
          "【自动】根据当前风扇配置（安静/降温）自动调节风扇转速。" +
          "程序每秒读取CPU和GPU温度，通过插值计算出目标转速。\n\n" +
          "【最大风扇】将风扇设置为最大转速。\n\n" +
          "【指定RPM】将风扇锁定在指定转速。可选范围：1600~6400 RPM。\n\n" +
          "注意：\n" +
          "- \"自动\"模式下，风扇转速取CPU和GPU对应转速的最大值\n" +
          "- 如果关闭了GPU监控，则只根据CPU温度调节\n" +
          "- 风扇控制需要管理员权限";

      // Performance Help
      PerformanceHelp.Text =
          "性能控制说明：\n\n" +
          "【狂暴模式 / 平衡模式】\n" +
          "对应HP BIOS中的性能模式切换。狂暴模式(0x31)释放更高功耗限制。\n\n" +
          "【GPU功率】\n" +
          "CTGP (CPU Total Graphics Power) 和 Dynamic Boost：\n" +
          "- CTGP开+DB开：最大GPU性能\n" +
          "- CTGP开+DB关：中等GPU性能\n" +
          "- CTGP关+DB关：最低GPU性能，适合省电\n\n" +
          "【CPU功率】\n" +
          "设置PL1+PL2功率限制。\"最大\"(254W)解除限制。" +
          "可按需设置10W~120W。需关闭温度阈值(throttlestop)后生效。\n\n" +
          "【切换DB版本】\n" +
          "解锁版本：替换NVIDIA Power Config Framework驱动以解锁更高GPU功耗。" +
          "需要连接电源并在CPU低负载下操作。解锁后重启需重新解锁（除非开启开机自启）。\n\n" +
          "【GPU频率限制】\n" +
          "使用nvidia-smi锁定GPU频率上限。适合控制功耗和温度。";

      // Other Help
      OtherHelp.Text =
          "其他功能说明：\n\n" +
          "【硬件监控】\n" +
          "GPU监控：自动检测GPU连接状态。连接外部显示器时自动开启，" +
          "低功耗状态自动关闭以节约资源。手动切换后当次不再自动变更。\n" +
          "风扇监控：开启后在浮窗和托盘提示中显示风扇转速。\n\n" +
          "【浮窗显示】\n" +
          "在屏幕角落显示硬件信息叠加层。支持左上角/右上角位置和24/36/48号字体大小。\n" +
          "浮窗完全穿透鼠标点击，不会影响正常操作。\n\n" +
          "【Omen键】\n" +
          "自定义笔记本Omen键功能：\n" +
          "- 默认：保持原始功能\n" +
          "- 切换浮窗显示：按Omen键切换浮窗的显示/隐藏\n" +
          "- 取消绑定：禁用Omen键\n\n" +
          "【图标】\n" +
          "- 原版：使用默认OMEN X Hub图标\n" +
          "- 自定义图标：使用程序目录下的custom.ico\n" +
          "- 动态图标：在托盘图标上实时显示CPU温度数值\n\n" +
          "【开机自启】\n" +
          "通过Windows任务计划程序以管理员权限自启动。" +
          "DB解锁用户建议开启此功能。";
    }
  }
}
