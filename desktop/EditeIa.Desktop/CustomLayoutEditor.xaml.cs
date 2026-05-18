using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace EditeIa.Desktop;

public partial class CustomLayoutEditor : UserControl
{
    public readonly record struct NormRect(double X, double Y, double W, double H);

    private enum DragMode { None, Move, Resize }

    private enum ResizeEdge
    {
        None,
        Move,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    private const double GripPx = 16;
    private const double MinRectPx = 32;

    private BitmapSource? _frame;
    private Rectangle _camRect = null!;
    private Rectangle _contentRect = null!;
    private NormRect _camNorm = new(0.25, 0.04, 0.38, 0.26);
    private NormRect _contentNorm = new(0.0, 0.30, 1.0, 0.68);
    private DragMode _dragMode;
    private ResizeEdge _resizeEdge;
    private Rectangle? _dragTarget;
    private Point _dragGrabOffset;
    private string? _pendingVideoPath;

    private const double NarrowEditorBreakpoint = 620;

    public CustomLayoutEditor()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InitOverlay();
            ApplyResponsiveLayout();
        };
    }

    private void CustomLayoutEditor_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded)
            return;

        var narrow = ActualWidth < NarrowEditorBreakpoint;

        if (narrow)
        {
            ColMidGap.Width = new GridLength(0);
            ColPreview.Width = new GridLength(1, GridUnitType.Star);

            Grid.SetColumn(FrameHost, 0);
            Grid.SetRow(FrameHost, 2);
            Grid.SetColumnSpan(FrameHost, 3);

            Grid.SetColumn(PreviewPanel, 0);
            Grid.SetRow(PreviewPanel, 3);
            Grid.SetColumnSpan(PreviewPanel, 3);
        }
        else
        {
            ColMidGap.Width = new GridLength(14);
            ColPreview.Width = GridLength.Auto;

            Grid.SetColumn(FrameHost, 0);
            Grid.SetRow(FrameHost, 2);
            Grid.SetColumnSpan(FrameHost, 1);

            Grid.SetColumn(PreviewPanel, 2);
            Grid.SetRow(PreviewPanel, 2);
            Grid.SetColumnSpan(PreviewPanel, 1);
        }
    }

    public bool HasFrame => _frame != null;

    public NormRect CameraNorm => ClampNorm(_camNorm);

    public NormRect ContentNorm => ClampNorm(_contentNorm);

    /// <summary>Ruta del último video usado para capturar (puede no coincidir si el usuario cambió el path sin recapturar).</summary>
    public string? LastCapturedVideoPath { get; private set; }

    public void SetPendingVideoPath(string? path) => _pendingVideoPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    /// <summary>Se dispara al capturar fotograma o al mover/redimensionar zonas.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Composición 9:16 (30% cámara + 70% juego) para la vista previa lateral.</summary>
    public ImageSource? GetVerticalStackPreview(int outW = 142, int outH = 252)
    {
        if (_frame is null || outW < 2 || outH < 2)
            return null;

        int iw = _frame.PixelWidth;
        int ih = _frame.PixelHeight;
        var top = MakeCrop(NormToPixelRect(CameraNorm, iw, ih));
        var bot = MakeCrop(NormToPixelRect(ContentNorm, iw, ih));
        int hTop = (int)Math.Round(outH * 0.30);
        int hBot = outH - hTop;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, outW, outH));
            DrawUniformFit(dc, top, new Rect(0, 0, outW, hTop));
            DrawUniformFit(dc, bot, new Rect(0, hTop, outW, hBot));
        }

        var rtb = new RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void DrawUniformFit(DrawingContext dc, BitmapSource src, Rect dest)
    {
        double sw = src.PixelWidth;
        double sh = src.PixelHeight;
        if (sw < 1 || sh < 1)
            return;

        double scale = Math.Min(dest.Width / sw, dest.Height / sh);
        double w = sw * scale;
        double h = sh * scale;
        double x = dest.X + (dest.Width - w) / 2;
        double y = dest.Y + (dest.Height - h) / 2;
        dc.DrawImage(src, new Rect(x, y, w, h));
    }

    private void NotifyLayoutChanged() => LayoutChanged?.Invoke(this, EventArgs.Empty);

    private void InitOverlay()
    {
        _contentRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(55, 255, 152, 0)),
            IsHitTestVisible = true,
        };
        _camRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0, 200, 83)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(55, 0, 200, 83)),
            IsHitTestVisible = true,
        };
        OverlayCanvas.Children.Add(_contentRect);
        OverlayCanvas.Children.Add(_camRect);
    }

    private static NormRect ClampNorm(NormRect r)
    {
        var x = Math.Clamp(r.X, 0, 0.999);
        var y = Math.Clamp(r.Y, 0, 0.999);
        var w = Math.Clamp(r.W, 0.04, 1 - x);
        var h = Math.Clamp(r.H, 0.04, 1 - y);
        return new NormRect(x, y, w, h);
    }

    private void ApplyFrame(BitmapSource bmp)
    {
        _frame = bmp;
        FrameImage.Source = bmp;
        FrameRoot.Width = bmp.PixelWidth;
        FrameRoot.Height = bmp.PixelHeight;
        OverlayCanvas.Width = bmp.PixelWidth;
        OverlayCanvas.Height = bmp.PixelHeight;
        FrameViewbox.Visibility = Visibility.Visible;
        SyncRectsFromNorm();
        UpdatePreview();
        FrameStatusText.Text = $"Fotograma {bmp.PixelWidth}×{bmp.PixelHeight} px — encuadre completo visible";
    }

    private void SyncRectsFromNorm()
    {
        if (OverlayCanvas.Width <= 0 || _frame is null)
            return;

        var cn = ClampNorm(_camNorm);
        var bn = ClampNorm(_contentNorm);
        _camNorm = cn;
        _contentNorm = bn;

        int iw = _frame.PixelWidth;
        int ih = _frame.PixelHeight;

        ApplyPixelRect(_contentRect, NormToPixelRect(bn, iw, ih));
        ApplyPixelRect(_camRect, NormToPixelRect(cn, iw, ih));
    }

    private static void ApplyPixelRect(Rectangle r, Int32Rect px)
    {
        Canvas.SetLeft(r, px.X);
        Canvas.SetTop(r, px.Y);
        r.Width = Math.Max(MinRectPx, px.Width);
        r.Height = Math.Max(MinRectPx, px.Height);
    }

    /// <summary>Mismos píxeles que usa FFmpeg (crop) y la vista previa.</summary>
    private static Int32Rect NormToPixelRect(NormRect n, int iw, int ih)
    {
        int x = (int)Math.Round(iw * n.X);
        int y = (int)Math.Round(ih * n.Y);
        int w = (int)Math.Round(iw * n.W);
        int h = (int)Math.Round(ih * n.H);
        x = Math.Clamp(x, 0, Math.Max(0, iw - 2));
        y = Math.Clamp(y, 0, Math.Max(0, ih - 2));
        w = Math.Clamp(w, 2, iw - x);
        h = Math.Clamp(h, 2, ih - y);
        return new Int32Rect(x, y, w, h);
    }

    private void NormFromRects()
    {
        double cw = OverlayCanvas.Width;
        double ch = OverlayCanvas.Height;
        if (cw <= 0 || ch <= 0)
            return;

        _contentNorm = ClampNorm(new NormRect(
            Canvas.GetLeft(_contentRect) / cw,
            Canvas.GetTop(_contentRect) / ch,
            _contentRect.Width / cw,
            _contentRect.Height / ch));

        _camNorm = ClampNorm(new NormRect(
            Canvas.GetLeft(_camRect) / cw,
            Canvas.GetTop(_camRect) / ch,
            _camRect.Width / cw,
            _camRect.Height / ch));
    }

    private void UpdatePreview()
    {
        if (_frame is null)
            return;

        try
        {
            int iw = _frame.PixelWidth;
            int ih = _frame.PixelHeight;
            PreviewTop.Source = MakeCrop(NormToPixelRect(CameraNorm, iw, ih));
            PreviewBottom.Source = MakeCrop(NormToPixelRect(ContentNorm, iw, ih));
        }
        catch
        {
            PreviewTop.Source = null;
            PreviewBottom.Source = null;
        }

        NotifyLayoutChanged();
    }

    private CroppedBitmap MakeCrop(Int32Rect px) =>
        new(_frame!, px);

    private (Rectangle? rect, ResizeEdge edge) HitTestRectAndEdge(Point p)
    {
        for (var i = OverlayCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (OverlayCanvas.Children[i] is not Rectangle r)
                continue;

            var edge = HitTestEdge(r, p);
            if (edge != ResizeEdge.None)
                return (r, edge);
        }

        return (null, ResizeEdge.None);
    }

    private static ResizeEdge HitTestEdge(Rectangle r, Point p)
    {
        var L = Canvas.GetLeft(r);
        var T = Canvas.GetTop(r);
        var R = L + r.Width;
        var B = T + r.Height;
        var g = GripPx;

        var onLeft = p.X >= L - g * 0.5 && p.X <= L + g;
        var onRight = p.X >= R - g && p.X <= R + g * 0.5;
        var onTop = p.Y >= T - g * 0.5 && p.Y <= T + g;
        var onBottom = p.Y >= B - g && p.Y <= B + g * 0.5;
        var insideX = p.X > L + g && p.X < R - g;
        var insideY = p.Y > T + g && p.Y < B - g;

        if (onLeft && onTop)
            return ResizeEdge.TopLeft;
        if (onRight && onTop)
            return ResizeEdge.TopRight;
        if (onLeft && onBottom)
            return ResizeEdge.BottomLeft;
        if (onRight && onBottom)
            return ResizeEdge.BottomRight;
        if (onTop && insideX)
            return ResizeEdge.Top;
        if (onBottom && insideX)
            return ResizeEdge.Bottom;
        if (onLeft && insideY)
            return ResizeEdge.Left;
        if (onRight && insideY)
            return ResizeEdge.Right;
        if (p.X >= L && p.X <= R && p.Y >= T && p.Y <= B)
            return ResizeEdge.Move;

        return ResizeEdge.None;
    }

    private static Cursor CursorForEdge(ResizeEdge edge) => edge switch
    {
        ResizeEdge.Move => Cursors.SizeAll,
        ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
        ResizeEdge.Top or ResizeEdge.Bottom => Cursors.SizeNS,
        ResizeEdge.TopLeft or ResizeEdge.BottomRight => Cursors.SizeNWSE,
        ResizeEdge.TopRight or ResizeEdge.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    private void ApplyResize(Rectangle r, ResizeEdge edge, Point p)
    {
        var maxW = OverlayCanvas.Width;
        var maxH = OverlayCanvas.Height;
        var L = Canvas.GetLeft(r);
        var T = Canvas.GetTop(r);
        var W = r.Width;
        var H = r.Height;
        var R = L + W;
        var B = T + H;

        switch (edge)
        {
            case ResizeEdge.Right:
                W = Math.Clamp(p.X - L, MinRectPx, maxW - L);
                break;
            case ResizeEdge.Bottom:
                H = Math.Clamp(p.Y - T, MinRectPx, maxH - T);
                break;
            case ResizeEdge.Left:
                L = Math.Clamp(p.X, 0, R - MinRectPx);
                W = R - L;
                break;
            case ResizeEdge.Top:
                T = Math.Clamp(p.Y, 0, B - MinRectPx);
                H = B - T;
                break;
            case ResizeEdge.TopLeft:
                L = Math.Clamp(p.X, 0, R - MinRectPx);
                T = Math.Clamp(p.Y, 0, B - MinRectPx);
                W = R - L;
                H = B - T;
                break;
            case ResizeEdge.TopRight:
                T = Math.Clamp(p.Y, 0, B - MinRectPx);
                H = B - T;
                W = Math.Clamp(p.X - L, MinRectPx, maxW - L);
                break;
            case ResizeEdge.BottomLeft:
                L = Math.Clamp(p.X, 0, R - MinRectPx);
                W = R - L;
                H = Math.Clamp(p.Y - T, MinRectPx, maxH - T);
                break;
            case ResizeEdge.BottomRight:
                W = Math.Clamp(p.X - L, MinRectPx, maxW - L);
                H = Math.Clamp(p.Y - T, MinRectPx, maxH - T);
                break;
        }

        Canvas.SetLeft(r, L);
        Canvas.SetTop(r, T);
        r.Width = W;
        r.Height = H;
    }

    private void UpdateHoverCursor(Point p)
    {
        if (_frame is null)
        {
            OverlayCanvas.Cursor = Cursors.Arrow;
            return;
        }

        var (_, edge) = HitTestRectAndEdge(p);
        OverlayCanvas.Cursor = CursorForEdge(edge);
    }

    private void OverlayCanvas_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_frame is null)
            return;

        var p = e.GetPosition(OverlayCanvas);
        var (rect, edge) = HitTestRectAndEdge(p);
        if (rect is null || edge == ResizeEdge.None)
            return;

        _dragTarget = rect;
        _resizeEdge = edge;
        _dragMode = edge == ResizeEdge.Move ? DragMode.Move : DragMode.Resize;
        _dragGrabOffset = new Point(
            p.X - Canvas.GetLeft(rect),
            p.Y - Canvas.GetTop(rect));
        rect.CaptureMouse();
    }

    private void OverlayCanvas_OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(OverlayCanvas);

        if (_dragMode == DragMode.None || _dragTarget is null || _frame is null)
        {
            UpdateHoverCursor(p);
            return;
        }

        if (_dragMode == DragMode.Move)
        {
            var nx = Math.Clamp(p.X - _dragGrabOffset.X, 0, OverlayCanvas.Width - _dragTarget.Width);
            var ny = Math.Clamp(p.Y - _dragGrabOffset.Y, 0, OverlayCanvas.Height - _dragTarget.Height);
            Canvas.SetLeft(_dragTarget, nx);
            Canvas.SetTop(_dragTarget, ny);
        }
        else
        {
            ApplyResize(_dragTarget, _resizeEdge, p);
        }

        NormFromRects();
        UpdatePreview();
    }

    private void StopDrag()
    {
        if (_dragTarget is null)
            return;
        _dragTarget.ReleaseMouseCapture();
        _dragTarget = null;
        _dragMode = DragMode.None;
        _resizeEdge = ResizeEdge.None;
        NormFromRects();
        SyncRectsFromNorm();
        UpdatePreview();
    }

    private void OverlayCanvas_OnMouseUp(object sender, MouseButtonEventArgs e) => StopDrag();

    private void OverlayCanvas_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragMode != DragMode.None && e.LeftButton != MouseButtonState.Pressed)
            StopDrag();
        else
            OverlayCanvas.Cursor = Cursors.Arrow;
    }

    private void CaptureBtn_Click(object sender, RoutedEventArgs e)
    {
        var path = _pendingVideoPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show("Elegí un archivo de video válido en «Video de entrada».", "edite_ia", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"edite_layout_{Guid.NewGuid():N}.png");
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments =
                    $"-hide_banner -loglevel error -y -ss 0.5 -i \"{path}\" -frames:v 1 \"{png}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using (var p = Process.Start(psi))
            {
                p?.WaitForExit(60000);
                if (p is { ExitCode: not 0 })
                {
                    FrameStatusText.Text = "FFmpeg no pudo extraer fotograma.";
                    return;
                }
            }

            if (!File.Exists(png))
            {
                FrameStatusText.Text = "No se generó imagen.";
                return;
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(png);
            bmp.EndInit();
            bmp.Freeze();
            ApplyFrame(bmp);
            LastCapturedVideoPath = path;
        }
        catch (Exception ex)
        {
            FrameStatusText.Text = "Error: " + ex.Message;
        }
    }

    public void ResetIfVideoChanged(string currentVideoPath)
    {
        var t = currentVideoPath.Trim();
        if (string.IsNullOrEmpty(t))
            return;
        if (!string.Equals(LastCapturedVideoPath, t, StringComparison.OrdinalIgnoreCase))
            FrameStatusText.Text = "Video cambió: volvé a «Capturar fotograma».";
    }
}
