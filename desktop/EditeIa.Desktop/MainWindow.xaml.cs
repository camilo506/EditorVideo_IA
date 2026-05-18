using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EditeIa.Desktop;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromHours(6) };
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _previewUiTimer;
    private string? _activeJobId;
    private string _verticalTemplate = "full";
    private bool _previewPlaying;
    private bool _timelineScrubbing;
    private bool _updatingTimelineFromMedia;

    private const string DefaultApiUrl = "http://127.0.0.1:8765";
    private const string DefaultModelSize = "small";
    private const int DefaultMaxClips = 8;
    private const double NarrowLayoutBreakpoint = 960;
    private const double CollapseSidebarBreakpoint = 720;
    private const double MinPlayerHeight = 120;

    public MainWindow()
    {
        InitializeComponent();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await PollJobAsync();
        _previewUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewUiTimer.Tick += (_, _) => RefreshPreviewTime();

        SourceInitialized += (_, _) => WindowTheme.ApplyDarkTitleBar(this);
        VideoPathBox.TextChanged += (_, _) => UpdateChromeForVideoPath();
        VideoPathBox.LostFocus += (_, _) => OnVideoPathForLayoutCommitted();
        Loaded += (_, _) =>
        {
            ApplyTemplateSelectionBorder(TplCardFull);
            LayoutEditor.LayoutChanged += (_, _) => RefreshSidebarVerticalPreview();
            RefreshLayoutEditorVisibility();
            UpdateChromeForVideoPath();
            ApplyResponsiveLayout();
            ScheduleMainPanelLayout();
        };
        Closed += (_, _) =>
        {
            _previewUiTimer.Stop();
            HorizontalPreviewMedia.Close();
            SidebarPreviewMedia.Close();
            SidebarCamMedia.Close();
            SidebarGameMedia.Close();
        };
    }

    /// <summary>Verde = listo; naranja = procesando (envío o trabajo en el backend).</summary>
    private void SetProcessingIndicator(bool busy)
    {
        StatusIndicatorCircle.Fill = new SolidColorBrush(
            Color.FromRgb(busy ? (byte)255 : (byte)102, busy ? (byte)152 : (byte)187, busy ? (byte)0 : (byte)106));
        StatusIndicatorCircle.ToolTip = busy ? "Procesando…" : "Listo";
    }

    private void SetStartButtonsEnabled(bool enabled)
    {
        StartButton.IsEnabled = enabled;
        ProcessOptionsButton.IsEnabled = enabled;
    }

    private string GetSelectedVerticalTemplate() => _verticalTemplate;

    private void TplCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string tag)
            return;
        _verticalTemplate = tag;
        ApplyTemplateSelectionBorder(b);
        RefreshLayoutEditorVisibility();
        RefreshSidebarVerticalPreview();
        e.Handled = true;
    }

    private void CambiarArea_Click(object sender, RoutedEventArgs e)
    {
        _verticalTemplate = "custom";
        ApplyTemplateSelectionBorder(TplCardCustom);
        RefreshLayoutEditorVisibility();
        RefreshSidebarVerticalPreview();
        Dispatcher.BeginInvoke(
            new Action(() => LayoutEditor.BringIntoView()),
            DispatcherPriority.ContextIdle);
    }

    private void ApplyTemplateSelectionBorder(Border selected)
    {
        var subtle = TryFindResource("Brush.BorderSubtle") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x3E));
        var card = TryFindResource("Brush.CardBg") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x30));
        var accent = TryFindResource("Brush.Accent") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x29, 0x79, 0xFF));

        void Reset(Border b)
        {
            b.BorderBrush = subtle;
            b.BorderThickness = new Thickness(1);
            b.Background = card;
        }

        Reset(TplCardFull);
        Reset(TplCardSplit);
        Reset(TplCardFaceTop);
        Reset(TplCardCustom);

        selected.BorderBrush = accent;
        selected.BorderThickness = new Thickness(2);
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
        ScheduleMainPanelLayout();
    }

    private void HorizontalPlayerHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ScheduleMainPanelLayout();

    private void ScheduleMainPanelLayout()
    {
        if (!IsLoaded)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            UpdateHorizontalPlayerHeight();
            UpdateMainScrollBehavior();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateHorizontalPlayerHeight()
    {
        if (HorizontalPlayerHost is null || PlayerOptionsGrid is null)
            return;

        var w = HorizontalPlayerHost.ActualWidth;
        if (w < 1)
            return;

        PlayerOptionsGrid.UpdateLayout();
        var gridH = PlayerOptionsGrid.ActualHeight;
        if (gridH < 1)
            return;

        var controlsH = (PlayerControlsGrid?.ActualHeight ?? 44) + 20;
        var maxByHeight = gridH - controlsH;

        if (ActualWidth < NarrowLayoutBreakpoint && OptionsPanel is not null)
        {
            OptionsPanel.UpdateLayout();
            maxByHeight -= OptionsPanel.ActualHeight + 12;
        }

        var ideal = w * 9.0 / 16.0;
        var h = Math.Min(ideal, maxByHeight);
        h = Math.Max(MinPlayerHeight, h);

        HorizontalPlayerHost.ClearValue(FrameworkElement.WidthProperty);
        HorizontalPlayerHost.Height = h;
    }

    private void UpdateMainScrollBehavior()
    {
        if (MainScrollViewer is null)
            return;

        MainScrollViewer.VerticalScrollBarVisibility =
            LayoutEditor.Visibility == Visibility.Visible
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded || EditorRoot.Visibility != Visibility.Visible)
            return;

        var windowW = ActualWidth;
        var narrow = windowW < NarrowLayoutBreakpoint;
        var hideSidebar = windowW < CollapseSidebarBreakpoint;

        if (PlayerOptionsGrid is not null && PlayerHostPanel is not null && OptionsPanel is not null)
        {
            if (narrow)
            {
                Grid.SetColumn(PlayerHostPanel, 0);
                Grid.SetRow(PlayerHostPanel, 0);
                Grid.SetColumnSpan(PlayerHostPanel, 3);

                Grid.SetColumn(OptionsPanel, 0);
                Grid.SetRow(OptionsPanel, 1);
                Grid.SetColumnSpan(OptionsPanel, 3);

                ColGap.Width = new GridLength(0);
                ColOptions.Width = new GridLength(1, GridUnitType.Star);
                ColPlayer.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                Grid.SetColumn(PlayerHostPanel, 0);
                Grid.SetRow(PlayerHostPanel, 0);
                Grid.SetColumnSpan(PlayerHostPanel, 1);

                Grid.SetColumn(OptionsPanel, 2);
                Grid.SetRow(OptionsPanel, 0);
                Grid.SetColumnSpan(OptionsPanel, 1);

                ColGap.Width = new GridLength(16);
                ColOptions.Width = new GridLength(280);
                ColPlayer.Width = new GridLength(1, GridUnitType.Star);
            }
        }

        if (SidebarPanel is not null)
            SidebarPanel.Visibility = hideSidebar ? Visibility.Collapsed : Visibility.Visible;
        if (SidebarColumn is not null)
            SidebarColumn.Width = hideSidebar ? new GridLength(0) : new GridLength(260);

        ScheduleMainPanelLayout();
    }

    private void UpdateChromeForVideoPath()
    {
        var path = VideoPathBox.Text.Trim();
        var exists = path.Length > 0 && File.Exists(path);
        LandingPanel.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        EditorRoot.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
        VideoTitleText.Text = exists ? Path.GetFileName(path) : string.Empty;
        VideoTitleText.ToolTip = exists ? path : null;
        SyncPreviewMedia();
        RefreshSidebarVerticalPreview();
        ApplyResponsiveLayout();
    }

    private bool IsCustomZonePreviewMode() =>
        string.Equals(GetSelectedVerticalTemplate(), "custom", StringComparison.Ordinal)
        && LayoutEditor.HasFrame;

    private void RefreshSidebarVerticalPreview()
    {
        if (!IsLoaded)
            return;

        var tpl = GetSelectedVerticalTemplate();

        if (IsCustomZonePreviewMode())
        {
            SidebarCustomPreviewRoot.Visibility = Visibility.Visible;
            SidebarPreviewMedia.Visibility = Visibility.Collapsed;
            SidebarPreviewHint.Text =
                "Salida 9:16 según tus zonas (30% / 70%). Sincronizada con la reproducción horizontal.";
            SyncCustomSidebarSources();
            UpdateSidebarCustomCropLayout();
            SyncCustomSidebarPlayback();
            return;
        }

        SidebarCustomPreviewRoot.Visibility = Visibility.Collapsed;
        SidebarPreviewMedia.Visibility = Visibility.Visible;
        SidebarFullCropCanvas.Visibility = Visibility.Visible;
        SidebarPreviewHint.Text = SidebarHintForTemplate(tpl);

        if (string.Equals(tpl, "full", StringComparison.Ordinal))
            UpdateSidebarFullCropLayout();
        else
            ResetSidebarPreviewMediaLayout();
    }

    /// <summary>Recorte 9:16 centrado (misma lógica que ffmpeg scale+crop en clip.py).</summary>
    private static CustomLayoutEditor.NormRect ComputeCenterCropNorm(double videoW, double videoH)
    {
        const double targetAspect = 9.0 / 16.0;
        var srcAspect = videoW / videoH;

        double cropW, cropH, x, y;
        if (srcAspect > targetAspect)
        {
            cropH = videoH;
            cropW = videoH * targetAspect;
            x = (videoW - cropW) / 2;
            y = 0;
        }
        else
        {
            cropW = videoW;
            cropH = videoW / targetAspect;
            x = 0;
            y = (videoH - cropH) / 2;
        }

        return new CustomLayoutEditor.NormRect(
            x / videoW,
            y / videoH,
            cropW / videoW,
            cropH / videoH);
    }

    private void UpdateSidebarFullCropLayout()
    {
        if (!IsLoaded || !string.Equals(GetSelectedVerticalTemplate(), "full", StringComparison.Ordinal))
            return;

        var vw = HorizontalPreviewMedia.NaturalVideoWidth;
        var vh = HorizontalPreviewMedia.NaturalVideoHeight;
        if (vw < 2 || vh < 2)
            return;

        var norm = ComputeCenterCropNorm(vw, vh);
        ApplyCropToCanvas(SidebarPreviewMedia, SidebarFullCropCanvas, norm, vw, vh);
    }

    private void ResetSidebarPreviewMediaLayout()
    {
        SidebarPreviewMedia.ClearValue(FrameworkElement.WidthProperty);
        SidebarPreviewMedia.ClearValue(FrameworkElement.HeightProperty);
        SidebarPreviewMedia.ClearValue(Canvas.LeftProperty);
        SidebarPreviewMedia.ClearValue(Canvas.TopProperty);
        SidebarPreviewMedia.Stretch = Stretch.UniformToFill;
    }

    private void SidebarFullCropCanvas_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateSidebarFullCropLayout();

    private static string SidebarHintForTemplate(string tpl) => tpl switch
    {
        "split_half" => "Mitad superior e inferior del mismo video, apiladas 9:16.",
        "face_top" => "Franja superior ~30% y resto abajo, apiladas 9:16.",
        "custom" => "Capturá un fotograma y ajustá los rectángulos verde y ámbar.",
        _ => "Mismo tiempo que la vista horizontal; recorte centrado 9:16.",
    };

    private void SyncCustomSidebarSources()
    {
        var uri = HorizontalPreviewMedia.Source;
        if (uri is null)
            return;

        if (SidebarCamMedia.Source != uri)
        {
            SidebarCamMedia.Source = uri;
            SidebarGameMedia.Source = uri;
        }
    }

    private void UpdateSidebarCustomCropLayout()
    {
        if (!IsCustomZonePreviewMode())
            return;

        var vw = HorizontalPreviewMedia.NaturalVideoWidth;
        var vh = HorizontalPreviewMedia.NaturalVideoHeight;
        if (vw < 2 || vh < 2)
            return;

        ApplyCropToCanvas(SidebarCamMedia, SidebarCamCanvas, LayoutEditor.CameraNorm, vw, vh);
        ApplyCropToCanvas(SidebarGameMedia, SidebarGameCanvas, LayoutEditor.ContentNorm, vw, vh);
    }

    private static void ApplyCropToCanvas(
        MediaElement media,
        Canvas canvas,
        CustomLayoutEditor.NormRect norm,
        double videoW,
        double videoH)
    {
        var hostW = canvas.ActualWidth;
        var hostH = canvas.ActualHeight;
        if (hostW < 2 || hostH < 2)
            return;

        var cropW = norm.W * videoW;
        var cropH = norm.H * videoH;
        if (cropW < 1 || cropH < 1)
            return;

        var scale = Math.Min(hostW / cropW, hostH / cropH);
        var scaledVideoW = videoW * scale;
        var scaledVideoH = videoH * scale;

        media.Width = scaledVideoW;
        media.Height = scaledVideoH;
        media.Stretch = Stretch.Fill;
        Canvas.SetLeft(media, -norm.X * videoW * scale);
        Canvas.SetTop(media, -norm.Y * videoH * scale);
    }

    private void SyncCustomSidebarPlayback()
    {
        if (!IsCustomZonePreviewMode())
            return;

        var pos = HorizontalPreviewMedia.Position;
        SidebarCamMedia.Position = pos;
        SidebarGameMedia.Position = pos;

        if (_previewPlaying)
        {
            SidebarCamMedia.Play();
            SidebarGameMedia.Play();
        }
        else
        {
            SidebarCamMedia.Pause();
            SidebarGameMedia.Pause();
        }
    }

    private void SidebarCropHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateSidebarCustomCropLayout();

    private void SidebarCropMedia_MediaOpened(object sender, RoutedEventArgs e)
    {
        UpdateSidebarCustomCropLayout();
        SyncCustomSidebarPlayback();
    }

    private void ApplyPreviewVolume()
    {
        var v = PreviewVolumeSlider.Value;
        HorizontalPreviewMedia.Volume = v;
        if (!IsCustomZonePreviewMode())
            SidebarPreviewMedia.Volume = v;
    }

    private void SyncPreviewMedia()
    {
        _previewPlaying = false;
        PreviewPlayBtn.Content = "▶";
        _previewUiTimer.Stop();

        var path = VideoPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            HorizontalPreviewMedia.Close();
            HorizontalPreviewMedia.Source = null;
            SidebarPreviewMedia.Close();
            SidebarPreviewMedia.Source = null;
            PreviewTimeText.Text = "— / —";
            ResetPreviewTimeline();
            return;
        }

        try
        {
            HorizontalPreviewMedia.Stop();
            SidebarPreviewMedia.Stop();
            SidebarCamMedia.Stop();
            SidebarGameMedia.Stop();
            var uri = new Uri(path, UriKind.Absolute);
            HorizontalPreviewMedia.Source = uri;
            SidebarPreviewMedia.Source = uri;
            SidebarCamMedia.Source = uri;
            SidebarGameMedia.Source = uri;
            ApplyPreviewVolume();
        }
        catch
        {
            PreviewTimeText.Text = "Error al abrir";
        }
    }

    private static string FormatShortTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
            t = TimeSpan.Zero;
        var h = (int)t.TotalHours;
        if (h > 0)
            return $"{h}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    private void RefreshPreviewTime()
    {
        if (HorizontalPreviewMedia.Source is null)
            return;

        if (IsCustomZonePreviewMode())
            SyncCustomSidebarPlayback();
        else if (_previewPlaying)
            SidebarPreviewMedia.Position = HorizontalPreviewMedia.Position;

        if (!HorizontalPreviewMedia.NaturalDuration.HasTimeSpan)
        {
            PreviewTimeText.Text = "…";
            return;
        }

        var total = HorizontalPreviewMedia.NaturalDuration.TimeSpan;
        if (total <= TimeSpan.Zero)
            return;

        var pos = HorizontalPreviewMedia.Position;
        PreviewTimeText.Text = $"{FormatShortTime(pos)} / {FormatShortTime(total)}";

        _updatingTimelineFromMedia = true;
        PreviewTimelineSlider.IsEnabled = true;
        PreviewTimelineSlider.Maximum = Math.Max(1, total.TotalSeconds);
        PreviewTimelineSlider.Value = Math.Clamp(pos.TotalSeconds, 0, PreviewTimelineSlider.Maximum);
        _updatingTimelineFromMedia = false;
    }

    private void ResetPreviewTimeline()
    {
        _updatingTimelineFromMedia = true;
        PreviewTimelineSlider.IsEnabled = false;
        PreviewTimelineSlider.Maximum = 1;
        PreviewTimelineSlider.Value = 0;
        _updatingTimelineFromMedia = false;
    }

    private void SeekPreviewTo(TimeSpan position)
    {
        if (HorizontalPreviewMedia.Source is null)
            return;

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        if (HorizontalPreviewMedia.NaturalDuration.HasTimeSpan)
        {
            var total = HorizontalPreviewMedia.NaturalDuration.TimeSpan;
            if (total > TimeSpan.Zero && position > total)
                position = total;
        }

        HorizontalPreviewMedia.Position = position;
        if (IsCustomZonePreviewMode())
        {
            SidebarCamMedia.Position = position;
            SidebarGameMedia.Position = position;
        }
        else
        {
            SidebarPreviewMedia.Position = position;
        }

        RefreshPreviewTime();
    }

    private void PreviewTimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _timelineScrubbing = true;

    private void PreviewTimelineSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _timelineScrubbing = false;
        if (!_updatingTimelineFromMedia)
            SeekPreviewTo(TimeSpan.FromSeconds(PreviewTimelineSlider.Value));
    }

    private void PreviewTimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _updatingTimelineFromMedia || !_timelineScrubbing)
            return;

        SeekPreviewTo(TimeSpan.FromSeconds(PreviewTimelineSlider.Value));
    }

    private void PreviewMedia_MediaOpened(object sender, RoutedEventArgs e)
    {
        ApplyPreviewVolume();
        UpdateSidebarFullCropLayout();
        RefreshSidebarVerticalPreview();
        RefreshPreviewTime();
        if (!_previewUiTimer.IsEnabled)
            _previewUiTimer.Start();
    }

    private void PreviewMedia_MediaEnded(object sender, RoutedEventArgs e)
    {
        _previewPlaying = false;
        PreviewPlayBtn.Content = "▶";
        HorizontalPreviewMedia.Pause();
        SidebarPreviewMedia.Pause();
        SidebarCamMedia.Pause();
        SidebarGameMedia.Pause();
        HorizontalPreviewMedia.Position = TimeSpan.Zero;
        SidebarPreviewMedia.Position = TimeSpan.Zero;
        SidebarCamMedia.Position = TimeSpan.Zero;
        SidebarGameMedia.Position = TimeSpan.Zero;
        RefreshPreviewTime();
    }

    private void PreviewMedia_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _previewPlaying = false;
        _previewUiTimer.Stop();
        PreviewPlayBtn.Content = "▶";
        PreviewTimeText.Text = "Codec no soportado";
        ResetPreviewTimeline();
    }

    private void PreviewPlay_Click(object sender, RoutedEventArgs e)
    {
        if (HorizontalPreviewMedia.Source is null)
            return;

        if (_previewPlaying)
        {
            HorizontalPreviewMedia.Pause();
            SidebarPreviewMedia.Pause();
            SidebarCamMedia.Pause();
            SidebarGameMedia.Pause();
            _previewPlaying = false;
            PreviewPlayBtn.Content = "▶";
        }
        else
        {
            var pos = HorizontalPreviewMedia.Position;
            if (IsCustomZonePreviewMode())
            {
                SidebarCamMedia.Position = pos;
                SidebarGameMedia.Position = pos;
                HorizontalPreviewMedia.Play();
                SidebarCamMedia.Play();
                SidebarGameMedia.Play();
            }
            else
            {
                SidebarPreviewMedia.Position = pos;
                HorizontalPreviewMedia.Play();
                SidebarPreviewMedia.Play();
            }

            _previewPlaying = true;
            PreviewPlayBtn.Content = "⏸";
        }

        RefreshPreviewTime();
    }

    private void PreviewVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;
        ApplyPreviewVolume();
    }

    private string GetSelectedClipLayout()
    {
        if (ClipLayoutCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag && tag.Length > 0)
            return tag;
        return "vertical";
    }

    private void RefreshLayoutEditorVisibility()
    {
        var custom = string.Equals(GetSelectedVerticalTemplate(), "custom", StringComparison.Ordinal);
        LayoutEditor.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom)
            LayoutEditor.SetPendingVideoPath(VideoPathBox.Text);
        RefreshSidebarVerticalPreview();
        ScheduleMainPanelLayout();
    }

    private void OnVideoPathForLayoutCommitted()
    {
        LayoutEditor.SetPendingVideoPath(VideoPathBox.Text);
        var t = VideoPathBox.Text.Trim();
        if (t.Length > 0)
            LayoutEditor.ResetIfVideoChanged(t);
    }

    private void BrowseVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Video|*.mp4;*.mkv;*.mov;*.webm;*.avi|Todos|*.*",
        };
        if (dlg.ShowDialog() == true)
        {
            VideoPathBox.Text = dlg.FileName;
            OnVideoPathForLayoutCommitted();
            UpdateChromeForVideoPath();
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Carpeta de salida",
            Multiselect = false,
        };
        if (dlg.ShowDialog() == true && dlg.FolderName is not null)
            OutputDirBox.Text = dlg.FolderName;
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var dir = OutputDirBox.Text.Trim();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("La carpeta de salida no existe.", "edite_ia", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_pollTimer.IsEnabled || _activeJobId is not null)
        {
            MessageBox.Show("Ya hay un trabajo en curso. Esperá a que termine.", "edite_ia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var video = VideoPathBox.Text.Trim();
        var outDir = OutputDirBox.Text.Trim();
        var baseUrl = DefaultApiUrl.TrimEnd('/');

        if (string.IsNullOrEmpty(video) || !File.Exists(video))
        {
            MessageBox.Show("Seleccioná un archivo de video válido.", "edite_ia", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(outDir))
        {
            MessageBox.Show("Indicá una carpeta de salida.", "edite_ia", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(outDir);

        var maxClips = DefaultMaxClips;

        var verticalTemplate = GetSelectedVerticalTemplate();
        if (string.Equals(verticalTemplate, "custom", StringComparison.Ordinal))
        {
            if (!LayoutEditor.HasFrame)
            {
                MessageBox.Show(
                    "Plantilla personalizada: capturá un fotograma con «Capturar fotograma» y ajustá los rectángulos de cámara y contenido.",
                    "edite_ia",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var capturedFor = LayoutEditor.LastCapturedVideoPath;
            if (string.IsNullOrEmpty(capturedFor) ||
                !string.Equals(video, capturedFor, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "El fotograma no corresponde al video de entrada actual. Volvé a pulsar «Capturar fotograma».",
                    "edite_ia",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        SetProcessingIndicator(true);

        CropRectDto? camDto = null;
        CropRectDto? contDto = null;
        if (string.Equals(verticalTemplate, "custom", StringComparison.Ordinal))
        {
            var cam = LayoutEditor.CameraNorm;
            var cont = LayoutEditor.ContentNorm;
            camDto = new CropRectDto { X = cam.X, Y = cam.Y, W = cam.W, H = cam.H };
            contDto = new CropRectDto { X = cont.X, Y = cont.Y, W = cont.W, H = cont.H };
        }

        var body = new JobCreateDto
        {
            InputPath = video,
            OutputDir = outDir,
            ModelSize = DefaultModelSize,
            Device = "cpu",
            ComputeType = "int8",
            Language = null,
            ClipLengthSec = 30,
            MaxClips = maxClips,
            Keywords = new List<string>(),
            Width = 1080,
            Height = 1920,
            BurnSubtitles = BurnSubsCheckBox.IsChecked == true,
            ClipLayout = GetSelectedClipLayout(),
            VerticalTemplate = verticalTemplate,
            CustomCamera = camDto,
            CustomContent = contDto,
        };

        SetStartButtonsEnabled(false);
        StatusBarText.Text = "Enviando trabajo…";

        try
        {
            var url = $"{baseUrl}/job";
            using var resp = await _http.PostAsJsonAsync(url, body, JsonOpts);
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                MessageBox.Show($"No se pudo crear el trabajo.\n{raw}", "edite_ia", MessageBoxButton.OK, MessageBoxImage.Error);
                SetProcessingIndicator(false);
                return;
            }

            var status = JsonSerializer.Deserialize<JobStatusDto>(raw, JsonOpts);
            if (status?.Id is null)
            {
                SetProcessingIndicator(false);
                return;
            }

            _activeJobId = status.Id;
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            SetProcessingIndicator(false);
            MessageBox.Show(
                "No se pudo contactar al backend. ¿Está corriendo edite-ia-api?\n\n" + ex.Message,
                "edite_ia",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (_activeJobId is null || !_pollTimer.IsEnabled)
            {
                SetStartButtonsEnabled(true);
                SetProcessingIndicator(false);
            }
        }
    }

    private async System.Threading.Tasks.Task PollJobAsync()
    {
        if (_activeJobId is null)
            return;

        var baseUrl = DefaultApiUrl.TrimEnd('/');
        try
        {
            var url = $"{baseUrl}/job/{_activeJobId}";
            var status = await _http.GetFromJsonAsync<JobStatusDto>(url, JsonOpts);
            if (status is null)
                return;

            StatusBarText.Text = $"{status.Status} — {status.Stage}";

            if (status.Status is "completed" or "failed")
            {
                _pollTimer.Stop();
                _activeJobId = null;
                SetStartButtonsEnabled(true);
                SetProcessingIndicator(false);

                if (status.Status == "completed")
                {
                    var detail = status.WorkDir is not null
                        ? $"\n\nCarpeta:\n{status.WorkDir}"
                        : "";
                    MessageBox.Show(
                        "Proceso terminado. Revisá la carpeta run_* dentro de la salida." + detail,
                        "edite_ia",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    var err = string.IsNullOrEmpty(status.Error)
                        ? "El proceso falló."
                        : $"El proceso falló.\n\n{status.Error}";
                    MessageBox.Show(err, "edite_ia", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        catch
        {
            // ignorar errores transitorios de red al sondear
        }
    }

    private sealed class JobCreateDto
    {
        [JsonPropertyName("input_path")]
        public string InputPath { get; set; } = "";

        [JsonPropertyName("output_dir")]
        public string OutputDir { get; set; } = "";

        [JsonPropertyName("model_size")]
        public string ModelSize { get; set; } = "small";

        [JsonPropertyName("device")]
        public string Device { get; set; } = "cpu";

        [JsonPropertyName("compute_type")]
        public string ComputeType { get; set; } = "int8";

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("clip_length_sec")]
        public double ClipLengthSec { get; set; }

        [JsonPropertyName("max_clips")]
        public int MaxClips { get; set; }

        [JsonPropertyName("keywords")]
        public List<string> Keywords { get; set; } = new();

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("burn_subtitles")]
        public bool BurnSubtitles { get; set; } = true;

        [JsonPropertyName("clip_layout")]
        public string ClipLayout { get; set; } = "vertical";

        [JsonPropertyName("vertical_template")]
        public string VerticalTemplate { get; set; } = "full";

        [JsonPropertyName("custom_camera")]
        public CropRectDto? CustomCamera { get; set; }

        [JsonPropertyName("custom_content")]
        public CropRectDto? CustomContent { get; set; }
    }

    private sealed class CropRectDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
    }

    private sealed class JobStatusDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("outputs")]
        public List<string> Outputs { get; set; } = new();

        [JsonPropertyName("work_dir")]
        public string? WorkDir { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
