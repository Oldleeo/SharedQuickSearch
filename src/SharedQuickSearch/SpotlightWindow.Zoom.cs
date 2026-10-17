using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace SharedQuickSearch;

/// <summary>
/// 空格预览里的缩放：滚轮以鼠标位置为中心放大缩小，放大后按住拖动，
/// 双击在“适应窗口”和“原始尺寸”之间切换；+ / - / 0 键也能用。
/// 放大超过当前图片的清晰度时，自动换一张更大的图（普通图片格式最长边 6000px）。
/// </summary>
public partial class SpotlightWindow
{
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private ScaleTransform? _zoomScale;
    private TranslateTransform? _zoomMove;
    private bool _panning;
    private Point _panMouse;
    private double _panStartX;
    private double _panStartY;
    private double _lookSourceEdge;
    private bool _fullResRequested;
    private CancellationTokenSource? _zoomCts;

    private bool IsZoomed => _zoom > 1.001;

    /// <summary>换图、打开、关闭时调用：回到适应窗口，不做动画。</summary>
    private void ResetZoomState()
    {
        EndPan();
        _zoomCts?.Cancel();
        _zoom = 1;
        _panX = _panY = 0;
        _zoomScale = null;
        _zoomMove = null;
        _zoomGroup = null;
        _lookSourceEdge = 0;
        _fullResRequested = false;
        LookImage.Cursor = null;
        LookZoomText.Text = "适应";
    }

    private void ResetZoom(bool animate)
    {
        if (!_lookOpen) return;
        _zoom = 1;
        _panX = _panY = 0;
        ApplyZoom(animate);
    }

    private void ZoomStep(double factor)
    {
        if (!_lookOpen) return;
        var area = LookArea();
        ZoomAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2), _zoom * factor, animate: true);
    }

    private void Look_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_lookOpen) return;
        e.Handled = true;
        // 一格滚轮约 1.2 倍；精确触控板的小增量也能连续缩放
        ZoomAt(e.GetPosition(LookCanvas), _zoom * Math.Pow(1.0016, e.Delta), animate: true);
    }

    /// <summary>以画布上的点 point 为中心缩放到 zoom 倍（相对适应窗口的大小）。</summary>
    private void ZoomAt(Point point, double zoom, bool animate)
    {
        var fit = FitRect();
        if (fit.Width <= 0) return;
        zoom = Math.Clamp(zoom, 1, MaxZoom(fit));
        // 鼠标下面那个像素缩放后还在鼠标下面
        var u = (point.X - fit.X - _panX) / _zoom;
        var v = (point.Y - fit.Y - _panY) / _zoom;
        _panX = point.X - fit.X - u * zoom;
        _panY = point.Y - fit.Y - v * zoom;
        _zoom = zoom;
        if (!IsZoomed) { _zoom = 1; _panX = _panY = 0; }
        ClampPan(fit);
        ApplyZoom(animate);
    }

    private Rect FitRect()
    {
        var left = Canvas.GetLeft(LookImage);
        var top = Canvas.GetTop(LookImage);
        return new Rect(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top, LookImage.Width is > 0 and var w ? w : 0, LookImage.Height is > 0 and var h ? h : 0);
    }

    /// <summary>最多放到原图像素的 4 倍，且至少能放大到适应尺寸的 4 倍，上限 16 倍。</summary>
    private double MaxZoom(Rect fit)
    {
        var entry = _lookItem?.Entry;
        var realEdge = entry is { Width: > 0, Height: > 0 } ? Math.Max(entry.Width, entry.Height) : Math.Max(_lookSourceEdge, CurrentSourceEdge());
        var fitEdge = Math.Max(fit.Width, fit.Height);
        if (fitEdge <= 0) return 4;
        return Math.Clamp(realEdge * 4 / fitEdge, 4, 16);
    }

    private double CurrentSourceEdge() => LookImage.Source is BitmapSource bitmap ? Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) : 0;

    /// <summary>图比可视区小就居中；比可视区大就不让边缘拖进可视区里面。</summary>
    private void ClampPan(Rect fit)
    {
        var area = LookArea();
        var width = fit.Width * _zoom;
        var height = fit.Height * _zoom;
        _panX = width <= area.Width
            ? area.X + (area.Width - width) / 2 - fit.X
            : Math.Clamp(_panX, area.Right - fit.X - width, area.X - fit.X);
        _panY = height <= area.Height
            ? area.Y + (area.Height - height) / 2 - fit.Y
            : Math.Clamp(_panY, area.Bottom - fit.Y - height, area.Y - fit.Y);
        if (!IsZoomed) _panX = _panY = 0;
    }

    private void ApplyZoom(bool animate)
    {
        if (_zoomScale is null || _zoomMove is null || !ReferenceEquals(LookImage.RenderTransform, _zoomGroup))
        {
            // 接管飞入动画留下的变换：从当前状态开始缩放
            _zoomScale = new ScaleTransform(1, 1);
            _zoomMove = new TranslateTransform();
            _zoomGroup = new TransformGroup { Children = { _zoomScale, _zoomMove } };
            LookImage.RenderTransform = _zoomGroup;
        }
        if (animate)
        {
            var duration = TimeSpan.FromMilliseconds(120);
            _zoomScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(_zoom, duration) { EasingFunction = EaseOut }, HandoffBehavior.SnapshotAndReplace);
            _zoomScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(_zoom, duration) { EasingFunction = EaseOut }, HandoffBehavior.SnapshotAndReplace);
            _zoomMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_panX, duration) { EasingFunction = EaseOut }, HandoffBehavior.SnapshotAndReplace);
            _zoomMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_panY, duration) { EasingFunction = EaseOut }, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            _zoomScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _zoomScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _zoomMove.BeginAnimation(TranslateTransform.XProperty, null);
            _zoomMove.BeginAnimation(TranslateTransform.YProperty, null);
            _zoomScale.ScaleX = _zoomScale.ScaleY = _zoom;
            _zoomMove.X = _panX;
            _zoomMove.Y = _panY;
        }
        LookImage.Cursor = IsZoomed ? Cursors.SizeAll : null;
        UpdateZoomText();
        _ = MaybeLoadFullResAsync();
    }

    private TransformGroup? _zoomGroup;

    private void UpdateZoomText()
    {
        if (!IsZoomed) { LookZoomText.Text = "适应"; return; }
        var fit = FitRect();
        var entry = _lookItem?.Entry;
        var realWidth = entry is { Width: > 0, Height: > 0 } ? entry.Width : (LookImage.Source as BitmapSource)?.PixelWidth ?? 0;
        LookZoomText.Text = realWidth > 0 && fit.Width > 0 ? $"{fit.Width * _zoom / realWidth * 100:F0}%" : $"{_zoom:F1}×";
    }

    /// <summary>放大到比当前图片还清楚的程度时，换一张更大的（只请求一次）。</summary>
    private async Task MaybeLoadFullResAsync()
    {
        var item = _lookItem;
        if (item is null || _fullResRequested || !IsZoomed) return;
        var entry = item.Entry;
        if (!FileTypes.Images.Contains(entry.Extension)) return;
        var fit = FitRect();
        var shownEdge = Math.Max(fit.Width, fit.Height) * _zoom;
        var sourceEdge = CurrentSourceEdge();
        var realEdge = entry is { Width: > 0, Height: > 0 } ? Math.Max(entry.Width, entry.Height) : 6000;
        if (shownEdge <= sourceEdge * 1.05 || realEdge <= sourceEdge * 1.05) return;
        _fullResRequested = true;
        _lookCts?.Cancel(); // 1600px 的那张还没到就不要了，省一次读共享盘
        _zoomCts?.Cancel();
        var cancellation = _zoomCts = new CancellationTokenSource();
        try
        {
            var (image, _, _) = await _host.Thumbnails.LoadPreviewImageAsync(entry.FullPath, (int)Math.Min(realEdge, 6000), cancellation.Token);
            if (image is null || cancellation.IsCancellationRequested || !_lookOpen || !ReferenceEquals(_lookItem, item)) return;
            LookImage.Source = image; // 比例相同，位置和缩放都不变
            _lookSourceEdge = Math.Max(image.PixelWidth, image.PixelHeight);
            UpdateZoomText();
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ---- 鼠标：双击切换 / 拖动 ----

    private void LookImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // 点在图上不关闭预览
        if (!_lookOpen) return;
        if (e.ClickCount == 2)
        {
            if (IsZoomed) { ResetZoom(animate: true); return; }
            var fit = FitRect();
            var entry = _lookItem?.Entry;
            var realWidth = entry is { Width: > 0, Height: > 0 } ? entry.Width : (LookImage.Source as BitmapSource)?.PixelWidth ?? 0;
            var actual = realWidth > 0 && fit.Width > 0 ? realWidth / fit.Width : 2;
            ZoomAt(e.GetPosition(LookCanvas), actual < 1.5 ? 2 : actual, animate: true);
            return;
        }
        if (!IsZoomed) return;
        _panning = true;
        _panMouse = e.GetPosition(LookCanvas);
        _panStartX = _panX;
        _panStartY = _panY;
        LookImage.CaptureMouse();
    }

    private void LookImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(LookCanvas);
        _panX = _panStartX + (position.X - _panMouse.X);
        _panY = _panStartY + (position.Y - _panMouse.Y);
        ClampPan(FitRect());
        ApplyZoom(animate: false);
    }

    private void LookImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndPan();
        e.Handled = true;
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        if (LookImage.IsMouseCaptured) LookImage.ReleaseMouseCapture();
    }
}
