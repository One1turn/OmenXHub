// LanShareWindow.xaml.cs - 局域网分享弹窗:二维码 + 分享链接 + 令牌策略
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using OmenSuperHub.Services;
using QRCoder;

namespace OmenSuperHub.Views {
  public partial class LanShareWindow : Wpf.Ui.Controls.FluentWindow {
    bool _loading;
    List<(string Name, string Ip)> _cands = new List<(string Name, string Ip)>();

    public LanShareWindow() {
      InitializeComponent();
      // ponytail: 令牌开关即时保存;Esc 关闭与 X 等价。
      KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
      Loaded += (s, e) => { _loading = true; LoadState(); _loading = false; };
    }

    void LoadState() {
      PersistentTokenCheck.IsChecked = ConfigService.HttpApiPersistentToken;
      Refresh();
    }

    void Refresh() {
      bool running = HardwareApiService.IsRunning;
      // ponytail: 未开启 API 时无监听/无令牌可分享,只显示提示并禁用交互控件。
      _loading = true;
      _cands = running ? HardwareApiService.GetLanIpCandidates() : new List<(string Name, string Ip)>();
      NicPicker.Items.Clear();
      foreach (var c in _cands) NicPicker.Items.Add($"{c.Name} ({c.Ip})");
      NicPicker.Visibility = _cands.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
      if (_cands.Count > 0) NicPicker.SelectedIndex = 0;
      _loading = false;
      UpdateLink();
      CopyButton.IsEnabled = running;
      ResetTokenButton.IsEnabled = running;
      StatusText.Text = running
        ? (ConfigService.LanAccessEnabled ? "" : Strings.LanNotEnabled)
        : Strings.LanNotEnabled;
    }

    void UpdateLink() {
      bool running = HardwareApiService.IsRunning;
      int idx = NicPicker.SelectedIndex;
      string ip = running && idx >= 0 && idx < _cands.Count ? _cands[idx].Ip : "";
      string url = running ? HardwareApiService.BuildShareUrl(ip) : "";
      LinkBox.Text = url;
      QrImage.Source = running ? RenderQr(url) : null;
      QrBorder.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    void NicPicker_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
      if (_loading) return;
      UpdateLink();
    }

    static BitmapImage RenderQr(string text) {
      try {
        using (var gen = new QRCodeGenerator())
        using (var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q)) {
          byte[] png = new PngByteQRCode(data).GetGraphic(8);
          var img = new BitmapImage();
          img.BeginInit();
          img.CacheOption = BitmapCacheOption.OnLoad;
          img.StreamSource = new MemoryStream(png);
          img.EndInit();
          img.Freeze();
          return img;
        }
      } catch (Exception ex) {
        Logger.Error($"LanShare QR render failed: {ex.Message}");
        return null;
      }
    }

    void Copy_Click(object sender, RoutedEventArgs e) {
      try {
        if (!string.IsNullOrEmpty(LinkBox.Text)) {
          Clipboard.SetText(LinkBox.Text);
          StatusText.Text = Strings.LanCopied;
        }
      } catch (Exception ex) { Logger.Warn($"Clipboard failed: {ex.Message}"); }
    }

    void PersistentToken_Changed(object sender, RoutedEventArgs e) {
      if (_loading) return;
      ConfigService.HttpApiPersistentToken = PersistentTokenCheck.IsChecked == true;
      ConfigService.Save("HttpApiPersistentToken");
      // 切到固定令牌且当前无存储令牌时,立刻落盘一个,后续重启复用。
      if (ConfigService.HttpApiPersistentToken && string.IsNullOrEmpty(ConfigService.HttpApiToken)) {
        ConfigService.HttpApiToken = Guid.NewGuid().ToString("N");
        ConfigService.Save("HttpApiToken");
      }
      Refresh();
    }

    void ResetToken_Click(object sender, RoutedEventArgs e) {
      HardwareApiService.ResetToken();
      Refresh();
    }
  }
}
