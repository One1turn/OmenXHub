using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace OmenSuperHub {
  public partial class HelpForm : Form {
    private static HelpForm _instance;

    public HelpForm() {
      Text = "OmenSuperHub";

      Rectangle screenBounds = Screen.PrimaryScreen.Bounds;
      Size formSize = new Size(screenBounds.Width / 2, screenBounds.Height / 2);
      this.Size = formSize;

      Point formLocation = new Point(
          (screenBounds.Width - formSize.Width) / 2,
          (screenBounds.Height - formSize.Height) / 2);

      this.StartPosition = FormStartPosition.Manual;
      this.Location = formLocation;
      Icon = Properties.Resources.fan;

      var panel = new Panel() {
        Dock = DockStyle.Fill,
        Padding = new Padding(12),
        AutoScroll = true,
        BackColor = SystemColors.Control
      };

      Assembly assembly = Assembly.GetExecutingAssembly();
      Version version = assembly.GetName().Version;

      var headerTextBox = new RichTextBox() {
        Dock = DockStyle.Top,
        Text = "版本号：" + version +
         "\n更新说明：\n" +
         "（1）添加对AMD CPU的硬件监控支持；\n" +
         "（2）下调了风扇配置“高”，“中”和“低”三档的响应速度，使转速变化能更加平缓；\n" +
         "（3）精简化菜单；\n" +
         "（4）更新“关于OXH”说明文档，文档现在按菜单顺序进行说明。\n\n",
        BorderStyle = BorderStyle.None,
        Font = new Font("Microsoft YaHei UI", 12, FontStyle.Regular),
        ReadOnly = true,
        BackColor = SystemColors.Control,
        ScrollBars = RichTextBoxScrollBars.None,
      };

      // 添加提示的LinkLabel和对应的Panel
      AddTip(panel, "七.   “开机自启”菜单说明",
        "（1）设置开机自启后，程序会自动创建任务计划程序实现开机自启，如果有自己设定过则应删除。同时，旧版OXH使用的自启方式也会在这一步被自动清除；\n" +
        "（2）关闭开机自启后，程序会清除任务计划程序");
      AddTip(panel, "六.   “图标”菜单说明",
        "（1）“原版”图标为程序自带图标；\n" +
        "（2）“自定义图标”需要在程序所在文件夹存放custom.ico图标文件才能使用；\n" +
        "（3）“动态图标”会以当前CPU温度作为图标，1秒刷新一次。");
      AddTip(panel, "五.   “Omen键”菜单说明",
        "（1）若选择“默认”，Omen键绑定的事件为任务计划程序的“Omen Key”任务，修改其启动程序可以自定义想要启动的程序；\n" +
        "（2）若选择“切换浮窗显示”，Omen键绑定的事件为启动OXH，通过传递特定参数使OXH获得指令并切换浮窗显示；\n" +
        "（3）若选择“取消绑定”，Omen键将无效。");
      AddTip(panel, "四.   “浮窗显示”菜单说明",
        "（1）开启“浮窗显示”后，屏幕左上角将覆盖硬件监控信息，1秒刷新一次。");
      AddTip(panel, "三.   “性能控制”菜单说明",
        "（1）“狂暴模式”和“平衡模式”在不同的机型上作用可能不同。对于暗9，平衡模式仅限制GPU（功耗不固定，70-110W均有可能），不影响CPU。注意，两种模式仅影响最大性能，对省电几乎没有影响，要省电应开启混合模式避免使用独显；\n" +
        "（2）显卡功耗=BTGP（基础功耗）+CTGP（可配置功耗）+DB（动态提升功耗），DB的含义是在CPU功率较低时额外提升GPU功耗，但在CPU功率较高时DB为0，在默认状态下，开启CTGP和DB才能获得最大GPU性能；\n" +
        "（3）DB版本指的是设备管理器-软件设备-NVIDIA Platform Controllers and Framework的驱动版本，解锁版本使用31.0.15.3730（来自537.42显卡驱动），注意旧显卡驱动不支持更新的DB驱动，否则会导致显卡锁定基础功耗，请使用最新显卡驱动；\n" +
        "（4）点击“解锁版本”，程序会删除解锁版本之外的DB驱动，只使用537.42对应DB驱动，然后启用再禁用驱动完成解锁，这会让显卡锁定当前功耗状态，即BTGP、CTGP和DB，利用这一点可以绕开CPU功率较高时DB为0的限制。点击“普通版本”会重新启用上述驱动，GPU功耗将取消锁定。注意更新NVIDIA显卡驱动后会更新DB驱动，需要重新解锁；\n" +
        "（5）由于其锁定功耗的特性，只有狂暴+CTGP开+DB开情况下才能使用切换菜单。同样地，请注意不要在双烤时切换解锁版本，否则也会导致解锁后只有CTGP开+DB关的功耗。系统重启后解锁会失效，需要由软件自动完成启用再禁用驱动的操作恢复解锁状态，因此还要启用OXH开机自启才能使用切换菜单。");
      AddTip(panel, "二.   “风扇控制”菜单说明",
        "（1）选择“自动”则程序会根据风扇配置和当前温度自动设定风扇转速，参考的温度值为cpu package温度和gpu温度+10的较大者，这是因为默认CPU温度墙97℃比GPU温度墙87℃高10℃；\n" +
        "（2）“最大风扇”为BIOS控制，不一定是最大转速，手动或自动有可能可以设置更高的转速；\n" +
        "（3）暗9笔记本转速范围0-6400，但是只有BIOS设置中关闭了风扇始终启动，才能低于2000转，请根据自己的机型设置合理的转速。");
      AddTip(panel, "一.   “风扇配置”菜单说明",
        "（1）本程序可设置两种不同的温度-转速对应配置，安静模式加载\"silent.txt\"，可以设置较为保守的风扇调度, 降温模式加载\"cool.txt\"，可以设置较为激进的风扇调度，打开silent和cool文件可自行修改风扇配置，格式为“60-2000”，左侧为温度，右侧为转速，行数不限，程序会自动进行线性插值，精度为1℃。例如，如果设置了50-3000和52-3200两个相邻点，程序会在51℃时设置3100的转速。注意，修改后需要重新点击对应的模式才能生效；\n" +
        "（2）读取到温度变化后程序将立即设置对应的转速，为了转速变化更加平缓，“实时”，“高”，“中”和“低”分别能以从无到高的强度对温度进行平滑处理。");

      panel.Controls.Add(headerTextBox);
      
      this.Controls.Add(panel);
    }

    private void AddTip(Panel panel, string tipTitle, string tipContent) {
      var linkLabel = new LinkLabel() {
        Text = tipTitle,
        Dock = DockStyle.Top,
        Font = new Font("Microsoft YaHei UI", 12, FontStyle.Underline),
        Margin = new Padding(0, 10, 0, 0), // 设置顶部间距
        AutoSize = true
      };

      var tipPanel = new Panel() {
        Dock = DockStyle.Top,
        BackColor = SystemColors.Control,
        Padding = new Padding(10),
        Margin = new Padding(0, 0, 0, 10), // 设置底部间距
        Visible = false // 默认隐藏
      };

      var contentLabel = new Label() {
        Text = tipContent,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Control,
        Font = new Font("Microsoft YaHei UI", 12, FontStyle.Regular),
      };

      tipPanel.Controls.Add(contentLabel);
      panel.Controls.Add(tipPanel);
      panel.Controls.Add(linkLabel);

      linkLabel.Click += (sender, e) => {
        tipPanel.Visible = !tipPanel.Visible; // 切换提示内容的可见性

        if (tipPanel.Visible) {
          tipPanel.Height = GetTextBoxHeight(contentLabel);
        }
      };
    }

    private int GetTextBoxHeight(Label textBox) {
      if (textBox == null || string.IsNullOrEmpty(textBox.Text)) {
        return 0;
      }

      using (Graphics g = textBox.CreateGraphics()) {
        // Measure the height of the text
        SizeF textSize = g.MeasureString(textBox.Text, textBox.Font, textBox.Width);

        // Calculate the number of lines
        int lineHeight = (int)g.MeasureString("A", textBox.Font).Height; // Measure height of one line
        int lineCount = (int)Math.Ceiling(textSize.Height / lineHeight);

        int fontHeight = textBox.Font.Height;
        int padding = textBox.Padding.Vertical;
        int height = (int)((lineCount * fontHeight * 1.1) + padding + 10); // 额外空间
        return height;
      }
    }

    public static HelpForm Instance {
      get {
        if (_instance == null || _instance.IsDisposed) {
          _instance = new HelpForm();
        }
        return _instance;
      }
    }

    private void HelpForm_FormClosed(object sender, FormClosedEventArgs e) {
      _instance = null;
    }
  }
}
