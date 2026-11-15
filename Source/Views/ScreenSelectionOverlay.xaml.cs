using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopIsland.Services;
using Forms = System.Windows.Forms;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace DesktopIsland.Views;

public partial class ScreenSelectionOverlay : Window
{
    private readonly Bitmap _virtualBmp;
    private readonly Rectangle _virtualPxBounds;
    private bool _isDragging;
    private WpfPoint _startDip;
    private WpfPoint _currentDip;

    public Bitmap? CroppedResult { get; private set; }

    public ScreenSelectionOverlay(string hintMessage)
    {
        InitializeComponent();
        HintText.Text = hintMessage;

        _virtualPxBounds = ScreenCaptureOcrService.GetVirtualBounds();
        _virtualBmp = ScreenCaptureOcrService.CaptureVirtualScreen();
        SnapshotImg.Source = ScreenCaptureOcrService.ToBitmapSource(_virtualBmp);

        Left = _virtualPxBounds.Left;
        Top = _virtualPxBounds.Top;
        Width = _virtualPxBounds.Width;
        Height = _virtualPxBounds.Height;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            var m = source.CompositionTarget.TransformFromDevice;
            var tl = m.Transform(new WpfPoint(_virtualPxBounds.Left, _virtualPxBounds.Top));
            var br = m.Transform(new WpfPoint(_virtualPxBounds.Right, _virtualPxBounds.Bottom));
            Left = tl.X;
            Top = tl.Y;
            Width = br.X - tl.X;
            Height = br.Y - tl.Y;
        }

        FullRectGeo.Rect = new WpfRect(0, 0, ActualWidth, ActualHeight);
        CutoutRectGeo.Rect = new WpfRect(0, 0, 0, 0);

        // Place top hint on primary screen center
        var primary = Forms.Screen.PrimaryScreen?.Bounds ?? _virtualPxBounds;
        double primaryCenterDipX = (primary.Left - _virtualPxBounds.Left + primary.Width / 2.0) * (ActualWidth / _virtualPxBounds.Width);
        double primaryTopDipY = (primary.Top - _virtualPxBounds.Top + 26) * (ActualHeight / _virtualPxBounds.Height);
        TopHintPill.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(TopHintPill, Math.Max(12, primaryCenterDipX - TopHintPill.DesiredSize.Width / 2.0));
        Canvas.SetTop(TopHintPill, Math.Max(12, primaryTopDipY));

        Activate();
        Focus();
    }

    private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _startDip = e.GetPosition(OverlayCanvas);
        _currentDip = _startDip;
        OverlayCanvas.CaptureMouse();
        SelectionFrame.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        UpdateSelectionVisual();
    }

    private void OverlayCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging) return;
        _currentDip = e.GetPosition(OverlayCanvas);
        UpdateSelectionVisual();
    }

    private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        OverlayCanvas.ReleaseMouseCapture();
        _currentDip = e.GetPosition(OverlayCanvas);

        double x = Math.Min(_startDip.X, _currentDip.X);
        double y = Math.Min(_startDip.Y, _currentDip.Y);
        double w = Math.Abs(_currentDip.X - _startDip.X);
        double h = Math.Abs(_currentDip.Y - _startDip.Y);

        if (w >= 6 && h >= 6 && ActualWidth > 0 && ActualHeight > 0)
        {
            double scaleX = _virtualBmp.Width / ActualWidth;
            double scaleY = _virtualBmp.Height / ActualHeight;
            var pxRect = new Rectangle(
                (int)Math.Round(x * scaleX),
                (int)Math.Round(y * scaleY),
                Math.Max(4, (int)Math.Round(w * scaleX)),
                Math.Max(4, (int)Math.Round(h * scaleY)));
            CroppedResult = ScreenCaptureOcrService.CropFromVirtual(_virtualBmp, pxRect);
            DialogResult = true;
        }
        else
        {
            DialogResult = false;
        }
        Close();
    }

    private void OverlayCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void UpdateSelectionVisual()
    {
        double x = Math.Min(_startDip.X, _currentDip.X);
        double y = Math.Min(_startDip.Y, _currentDip.Y);
        double w = Math.Abs(_currentDip.X - _startDip.X);
        double h = Math.Abs(_currentDip.Y - _startDip.Y);

        var rect = new WpfRect(x, y, Math.Max(1, w), Math.Max(1, h));
        CutoutRectGeo.Rect = rect;

        Canvas.SetLeft(SelectionFrame, x);
        Canvas.SetTop(SelectionFrame, y);
        SelectionFrame.Width = Math.Max(2, w);
        SelectionFrame.Height = Math.Max(2, h);

        double scaleX = ActualWidth > 0 ? _virtualBmp.Width / ActualWidth : 1;
        double scaleY = ActualHeight > 0 ? _virtualBmp.Height / ActualHeight : 1;
        SizeBadgeText.Text = $"{(int)Math.Round(w * scaleX)} × {(int)Math.Round(h * scaleY)} px";

        double badgeY = y + h + 8;
        if (badgeY + 28 > ActualHeight) badgeY = Math.Max(8, y - 30);
        Canvas.SetLeft(SizeBadge, Math.Min(ActualWidth - 110, x));
        Canvas.SetTop(SizeBadge, badgeY);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _virtualBmp.Dispose();
    }
}
