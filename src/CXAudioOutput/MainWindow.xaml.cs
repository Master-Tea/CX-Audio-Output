using System.Collections.ObjectModel;
using System.Diagnostics;
using CXAudioOutput.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;
using Path = System.IO.Path;

namespace CXAudioOutput;

public sealed partial class MainWindow : Window
{
    private readonly LaunchOptions _options;
    private readonly AppServer? _server;
    private readonly string? _serverConstructionError;
    private readonly ObservableCollection<NetworkAdapterViewModel> _adapterItems = new();
    private readonly ObservableCollection<ReceiverViewModel> _receiverItems = new();
    private readonly Dictionary<string, NetworkAdapterViewModel> _adapterMap =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReceiverViewModel> _receiverMap =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer? _noticeTimer;

    private readonly SolidColorBrush _acidBrush = new(Color.FromArgb(255, 216, 255, 101));
    private readonly SolidColorBrush _warningBrush = new(Color.FromArgb(255, 255, 193, 102));
    private readonly SolidColorBrush _dangerBrush = new(Color.FromArgb(255, 255, 116, 108));
    private readonly SolidColorBrush _mutedBrush = new(Color.FromArgb(255, 114, 123, 111));
    private readonly SolidColorBrush _meterBrush = new(Color.FromArgb(230, 216, 255, 101));
    private readonly SolidColorBrush _meterDimBrush = new(Color.FromArgb(55, 216, 255, 101));
    private readonly SolidColorBrush _baselineBrush = new(Color.FromArgb(40, 255, 255, 255));

    private bool _updating;
    private bool _loaded;
    private bool _serverStarted;
    private bool _closed;
    private string _networkSignature = string.Empty;
    private string _shareSignature = string.Empty;
    private string _qrUrl = string.Empty;
    private string _currentShareUrl = string.Empty;
    private int _qrGeneration;
    private List<ShareLink> _shareLinks = new();
    private double[]? _lastLevels;

    public MainWindow(LaunchOptions options)
    {
        _options = options;
        InitializeComponent();

        Title = "CX Audio Output";
        ConfigureWindow();
        PortText.Text = options.Port.ToString();
        SettingsPathText.Text = AppPaths.SettingsFile;
        WebUrlText.Text = $"http://127.0.0.1:{options.Port}/studio";

        AdapterList.ItemsSource = _adapterItems;
        ReceiverList.ItemsSource = _receiverItems;

        try
        {
            _server = new AppServer(options);
            _server.Network.Changed += Network_Changed;
        }
        catch (Exception ex)
        {
            _serverConstructionError = ex.Message;
            ShowNotice(InfoBarSeverity.Error, "服务未能启动", ex.Message);
            SetServicePill(false, "启动失败");
        }

        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(500);
        _refreshTimer.Tick += (_, _) => RefreshEverything();
        _refreshTimer.Start();

        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.Interval = TimeSpan.FromSeconds(4);
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick += (_, _) => ErrorBar.IsOpen = false;

        RootGrid.Loaded += RootGrid_Loaded;
        Closed += Window_Closed;

        _updating = true;
        LatencyComboBox.SelectedIndex = 1;
        _updating = false;
        UpdateNetwork(force: true);
        UpdateShare(force: true);
    }

    private void ConfigureWindow()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var appWindow = AppWindow;
            var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var width = Math.Min(1280, Math.Max(960, display.WorkArea.Width - 80));
            var height = Math.Min(860, Math.Max(700, display.WorkArea.Height - 80));
            var x = display.WorkArea.X + (display.WorkArea.Width - width) / 2;
            var y = display.WorkArea.Y + (display.WorkArea.Height - height) / 2;
            appWindow.MoveAndResize(new RectInt32(x, y, width, height));
            appWindow.Title = "CX Audio Output";
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

            var titleBar = appWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 241, 244, 233);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 114, 123, 111);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(30, 255, 255, 255);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(45, 255, 255, 255);
        }
        catch
        {
            // If the OS doesn't support a customized title bar, the default one is fine.
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await StartServerAsync();
    }

    private async Task StartServerAsync()
    {
        if (_server is null)
        {
            if (!string.IsNullOrWhiteSpace(_serverConstructionError))
            {
                ShowNotice(InfoBarSeverity.Error, "服务未能启动", _serverConstructionError);
            }

            return;
        }

        try
        {
            SetServicePill(false, "正在启动…");
            await _server.StartAsync();
            _serverStarted = true;
            SetServicePill(true, $"运行中 · :{_options.Port}");
            ErrorBar.IsOpen = false;
            UpdateNetwork(force: true);
            UpdateShare(force: true);
            RefreshEverything();
        }
        catch (Exception ex)
        {
            _serverStarted = false;
            SetServicePill(false, "启动失败");
            ShowNotice(InfoBarSeverity.Error,
                $"端口 {_options.Port} 无法监听",
                "请检查端口是否被占用，或使用 --port 指定另一个端口。\n" + ex.Message);
        }
    }

    private void RefreshEverything()
    {
        if (_closed) return;
        if (_server is null) return;

        UpdateAudioStatus();
        UpdateNetwork();
        UpdateReceivers();
        UpdateShare();
    }

    private void UpdateAudioStatus()
    {
        var snapshot = _server!.State.Snapshot();
        var capture = snapshot.Capture;
        var status = capture.Status;

        var (eyebrow, headline) = status switch
        {
            "live" => ("SYSTEM AUDIO · LIVE", "正在把声音送出去。"),
            "paused" => ("SYSTEM AUDIO · PAUSED", "串流已暂停。"),
            "starting" => ("SYSTEM AUDIO · STARTING", "正在准备系统音频。"),
            "no-device" => ("SYSTEM AUDIO · NO DEVICE", "没有找到输出设备。"),
            _ => ("SYSTEM AUDIO · ATTENTION", "音频设备需要检查。")
        };

        StatusEyebrow.Text = eyebrow;
        StatusHeadline.Text = headline;
        LiveDot.Fill = status switch
        {
            "live" => _acidBrush,
            "paused" or "starting" => _warningBrush,
            _ => _dangerBrush
        };

        if (status == "live")
        {
            SourceText.Text = "正在从 " + (capture.DeviceName ?? "默认输出设备") + " 捕获系统声音";
        }
        else if (status == "paused")
        {
            SourceText.Text = "串流已暂停，接收设备会显示暂停提示。";
        }
        else if (status == "no-device")
        {
            SourceText.Text = "请连接扬声器或耳机，应用会自动重试。";
        }
        else if (status == "error")
        {
            SourceText.Text = capture.Error ?? "音频捕获失败，正在自动重试。";
        }
        else
        {
            SourceText.Text = "正在连接默认音频输出设备…";
        }

        var rate = capture.SampleRate > 0 ? capture.SampleRate : 48000;
        SampleText.Text = $"{rate / 1000.0:0.#} kHz · STEREO · 16-BIT";
        var uptime = DateTimeOffset.UtcNow - _server.State.StartedAt;
        UptimeText.Text = $"已运行 {uptime:hh\\:mm\\:ss}";
        AudioDeviceText.Text = capture.DeviceName ?? "未检测到输出设备";

        var paused = status == "paused";
        PauseText.Text = paused ? "继续串流" : "暂停串流";
        PauseIcon.Glyph = paused ? "\uE768" : "\uE769";
        PauseButton.Background = paused
            ? _acidBrush
            : new SolidColorBrush(Color.FromArgb(12, 255, 255, 255));
        PauseButton.Foreground = paused
            ? new SolidColorBrush(Color.FromArgb(255, 10, 12, 8))
            : new SolidColorBrush(Color.FromArgb(255, 241, 244, 233));

        if (Math.Abs(MasterVolumeSlider.Value - snapshot.MasterVolume) > 0.5)
        {
            _updating = true;
            MasterVolumeSlider.Value = snapshot.MasterVolume;
            _updating = false;
        }

        MasterVolumeText.Text = snapshot.MasterVolume + "%";
        MuteIcon.Glyph = snapshot.Muted ? "\uE74F" : "\uE767";
        MuteButton.Opacity = snapshot.Muted ? 0.55 : 1;

        _updating = true;
        LatencyComboBox.SelectedIndex = snapshot.Profile switch
        {
            "low" => 0,
            "stable" => 2,
            _ => 1
        };
        _updating = false;
        LatencyText.Text = snapshot.TargetMs + " ms";

        var peak = snapshot.Peak;
        PeakText.Text = peak <= 0.00001
            ? "−∞ dB"
            : (20 * Math.Log10(peak)).ToString("0.0").Replace("-", "−") + " dB";
        _lastLevels = snapshot.Levels;
        DrawAudioMeter();
    }

    private void DrawAudioMeter()
    {
        if (_lastLevels is null) return;
        var width = AudioMeterCanvas.ActualWidth;
        var height = AudioMeterCanvas.ActualHeight;
        if (width < 8 || height < 8) return;

        AudioMeterCanvas.Children.Clear();
        const int barCount = 48;
        var gap = Math.Max(3, width / 220);
        var barWidth = Math.Max(2, (width - gap * (barCount - 1)) / barCount);
        var center = height * 0.56;
        var maxHeight = height * 0.72;
        var status = _server?.State.Capture.Status;
        var live = status == "live";
        var brush = live ? _meterBrush : _meterDimBrush;

        for (var i = 0; i < barCount; i += 1)
        {
            var sourceIndex = (int)Math.Round(i / (double)(barCount - 1) * (_lastLevels.Length - 1));
            var level = Math.Clamp(_lastLevels[sourceIndex], 0, 1);
            var amplitude = Math.Max(2, Math.Pow(level, 0.55) * maxHeight);
            var rectangle = new Rectangle
            {
                Width = barWidth,
                Height = amplitude,
                RadiusX = barWidth / 2,
                RadiusY = barWidth / 2,
                Fill = brush,
                Opacity = live ? 0.92 : 0.32
            };
            Canvas.SetLeft(rectangle, i * (barWidth + gap));
            Canvas.SetTop(rectangle, center - amplitude / 2);
            AudioMeterCanvas.Children.Add(rectangle);
        }

        var baseline = new Rectangle
        {
            Width = width,
            Height = 1,
            Fill = _baselineBrush
        };
        Canvas.SetLeft(baseline, 0);
        Canvas.SetTop(baseline, center);
        AudioMeterCanvas.Children.Add(baseline);
    }

    private void UpdateNetwork(bool force = false)
    {
        if (_server is null) return;
        var snapshot = _server.Network.GetSnapshot();
        UpdateStampText.Text = "硬件状态 · " + DateTime.Now.ToString("HH:mm:ss") + " 已更新";

        var signature = snapshot.Revision + "|" + snapshot.MultiSelect + "|" +
                        string.Join(",", snapshot.SelectedIds.OrderBy(id => id));
        if (!force && signature == _networkSignature) return;
        _networkSignature = signature;

        var liveIds = new HashSet<string>(snapshot.Adapters.Select(adapter => adapter.Id),
            StringComparer.OrdinalIgnoreCase);
        foreach (var existing in _adapterItems.Select(item => item.Id).ToList())
        {
            if (liveIds.Contains(existing)) continue;
            if (_adapterMap.Remove(existing, out var removed)) _adapterItems.Remove(removed);
        }

        foreach (var adapter in snapshot.Adapters)
        {
            if (!_adapterMap.TryGetValue(adapter.Id, out var viewModel))
            {
                viewModel = new NetworkAdapterViewModel();
                _adapterMap[adapter.Id] = viewModel;
                _adapterItems.Add(viewModel);
            }

            viewModel.Update(adapter, snapshot.SelectedIds.Contains(adapter.Id));
        }

        _updating = true;
        MultiSelectSwitch.IsOn = snapshot.MultiSelect;
        _updating = false;
        SelectAllButton.IsEnabled = snapshot.MultiSelect;

        var selectedCount = snapshot.SelectedIds.Count;
        var activeSelected = snapshot.Adapters.Count(adapter =>
            adapter.IsUp && snapshot.SelectedIds.Contains(adapter.Id));
        var addressCount = snapshot.Addresses.Count;

        NetworkSummaryText.Text = selectedCount == 0
            ? "仅本机可访问 · 其他设备无法连接"
            : $"已选择 {selectedCount} 个网卡 · {activeSelected} 个正在广播 · {addressCount} 个可用地址";

        if (selectedCount == 0)
        {
            ShowNetworkWarning("未选择网卡。其他设备无法连接，但本机控制台仍可使用。");
        }
        else if (addressCount == 0)
        {
            ShowNetworkWarning("所选网卡当前没有可用 IPv4 地址，或网卡已断开。请检查硬件状态。");
        }
        else
        {
            NetworkWarningBar.IsOpen = false;
        }

        UpdateShare(force: true);
    }

    private void UpdateReceivers()
    {
        if (_server is null) return;
        var snapshots = _server.Hub.Receivers
            .Select(receiver => receiver.Snapshot())
            .OrderBy(receiver => receiver.ConnectedAt)
            .ToList();

        var liveIds = new HashSet<string>(snapshots.Select(receiver => receiver.Id),
            StringComparer.OrdinalIgnoreCase);
        foreach (var existing in _receiverItems.Select(item => item.Id).ToList())
        {
            if (liveIds.Contains(existing)) continue;
            if (_receiverMap.Remove(existing, out var removed)) _receiverItems.Remove(removed);
        }

        foreach (var snapshot in snapshots)
        {
            if (!_receiverMap.TryGetValue(snapshot.Id, out var viewModel))
            {
                viewModel = new ReceiverViewModel();
                _receiverMap[snapshot.Id] = viewModel;
                _receiverItems.Add(viewModel);
            }

            viewModel.Update(snapshot);
        }

        ReceiverCountText.Text = snapshots.Count + " 台";
        ReceiverEmptyText.Visibility = snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReceiverList.Visibility = snapshots.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateShare(bool force = false)
    {
        if (_server is null) return;
        var code = _server.State.AccessCode;
        var addresses = _server.Network.GetAdvertisedAddresses();
        var key = code + "|" + string.Join(",", addresses.Select(address => address.Ip)) + "|" + _options.Port;
        if (!force && key == _shareSignature) return;
        _shareSignature = key;

        _shareLinks = addresses
            .Select(address => new ShareLink(
                address.Ip + "  ·  " + address.InterfaceName,
                $"http://{address.Ip}:{_options.Port}/listen?code={code}",
                address.Ip))
            .ToList();

        AccessCodeText.Text = code;
        _updating = true;
        AddressComboBox.ItemsSource = _shareLinks;
        if (_shareLinks.Count > 0)
        {
            var currentIndex = _shareLinks.FindIndex(link => link.Ip == _currentShareIp);
            AddressComboBox.SelectedIndex = currentIndex >= 0 ? currentIndex : 0;
        }
        else
        {
            AddressComboBox.SelectedIndex = -1;
        }
        _updating = false;

        AddressComboBox.IsEnabled = _shareLinks.Count > 0;
        CopyLinkButton.IsEnabled = _shareLinks.Count > 0;

        if (_shareLinks.Count == 0)
        {
            _currentShareUrl = string.Empty;
            ShareUrlText.Text = "没有可用网卡地址";
            QrPlaceholder.Visibility = Visibility.Visible;
            QrImage.Source = null;
            _qrUrl = string.Empty;
            return;
        }

        var selected = AddressComboBox.SelectedIndex >= 0
            ? _shareLinks[AddressComboBox.SelectedIndex]
            : _shareLinks[0];
        _currentShareIp = selected.Ip;
        _currentShareUrl = selected.Url;
        ShareUrlText.Text = selected.Url;
        _ = UpdateQrAsync(selected.Url);
    }

    private string _currentShareIp = string.Empty;

    private async Task UpdateQrAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url == _qrUrl) return;
        var generation = ++_qrGeneration;
        try
        {
            var image = await QrCodeService.CreateAsync(url);
            if (generation != _qrGeneration || _closed) return;
            QrImage.Source = image;
            QrPlaceholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
            _qrUrl = image is null ? string.Empty : url;
        }
        catch
        {
            if (generation != _qrGeneration) return;
            QrPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void Network_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            UpdateNetwork(force: true);
            UpdateShare(force: true);
        });
    }

    private void SetServicePill(bool online, string text)
    {
        ServicePillText.Text = text;
        ServiceDot.Fill = online ? _acidBrush : _warningBrush;
        ServicePill.Background = online
            ? (Brush)Application.Current.Resources["AcidSoftBrush"]
            : new SolidColorBrush(Color.FromArgb(20, 255, 193, 102));
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string message)
    {
        ErrorBar.Severity = severity;
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        if (severity == InfoBarSeverity.Success || severity == InfoBarSeverity.Informational)
        {
            _noticeTimer?.Start();
        }
    }

    private void ShowNetworkWarning(string message)
    {
        NetworkWarningBar.Message = message;
        NetworkWarningBar.IsOpen = true;
    }

    private void AudioMeter_SizeChanged(object sender, SizeChangedEventArgs e) => DrawAudioMeter();

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        _server.Capture.SetPaused(!_server.Capture.IsPaused);
        await Task.Delay(250);
        RefreshEverything();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        _server.Hub.SetMuted(!_server.State.Muted);
        RefreshEverything();
    }

    private void MasterVolume_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating || _server is null) return;
        _server.Hub.SetMasterVolume((int)Math.Round(e.NewValue));
        MasterVolumeText.Text = (int)Math.Round(e.NewValue) + "%";
    }

    private void Latency_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _server is null) return;
        var profile = (LatencyComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "balanced";
        _server.Hub.SetProfile(profile);
        LatencyText.Text = _server.State.TargetMs + " ms";
    }

    private async void AdapterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        if (sender is not CheckBox checkBox || checkBox.Tag is not string id) return;
        _server.Network.SetSelected(id, checkBox.IsChecked == true);
        await Task.Delay(30);
        UpdateNetwork(force: true);
        UpdateShare(force: true);
    }

    private void MultiSelect_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating || _server is null) return;
        _server.Network.SetMultiSelect(MultiSelectSwitch.IsOn);
        UpdateNetwork(force: true);
        UpdateShare(force: true);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        _server.Network.SelectAllActive();
        UpdateNetwork(force: true);
        UpdateShare(force: true);
    }

    private void ClearNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        _server.Network.ClearSelection();
        UpdateNetwork(force: true);
        UpdateShare(force: true);
    }

    private void RefreshNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        _server.Network.Refresh();
        UpdateNetwork(force: true);
        UpdateShare(force: true);
    }

    private void Address_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (AddressComboBox.SelectedItem is not ShareLink link) return;
        _currentShareIp = link.Ip;
        _currentShareUrl = link.Url;
        ShareUrlText.Text = link.Url;
        _ = UpdateQrAsync(link.Url);
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentShareUrl))
        {
            ShowNotice(InfoBarSeverity.Warning, "没有可用链接", "请先选择一个有 IPv4 地址的网卡。");
            return;
        }

        CopyText(_currentShareUrl, "收听链接已复制");
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        CopyText(_server.State.AccessCode, "访问码已复制");
    }

    private void RotateCode_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        _server.Hub.RotateCode();
        UpdateShare(force: true);
        ShowNotice(InfoBarSeverity.Success, "访问码已更换", "已经连接的设备不会断开，新设备需要使用新访问码。");
    }

    private void CopyText(string text, string notice)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowNotice(InfoBarSeverity.Success, "已复制", notice);
        }
        catch
        {
            ShowNotice(InfoBarSeverity.Error, "复制失败", "系统剪贴板当前不可用。");
        }
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null || !_serverStarted)
        {
            ShowNotice(InfoBarSeverity.Warning, "服务尚未启动", "请等待服务启动完成后再打开网页控制台。");
            return;
        }

        OpenExternal(_server.LocalStudioUrl);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureDirectories();
        OpenFolder(AppPaths.LogsDirectory);
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureDirectories();
        OpenFolder(AppPaths.Root);
    }

    private void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, "无法打开", ex.Message);
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, "无法打开文件夹", ex.Message);
        }
    }

    private void IdentifyReceiver_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        if (sender is not Button button || button.Tag is not string id) return;
        _server.Hub.Identify(id);
    }

    private async void KickReceiver_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        if (sender is not Button button || button.Tag is not string id) return;

        var dialog = new ContentDialog
        {
            Title = "断开这台设备？",
            Content = "该设备将立即停止播放，但可以重新扫码连接。",
            PrimaryButtonText = "断开",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _server.Hub.KickAsync(id);
        }
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        if (_closed) return;
        _closed = true;
        _refreshTimer.Stop();
        _noticeTimer?.Stop();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private sealed record ShareLink(string Display, string Url, string Ip);
}
