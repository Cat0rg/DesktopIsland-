using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopIsland.Services;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace DesktopIsland.Views;

public partial class ScreenColorPickerOverlay : Window
{
    private readonly Bitmap _virtualBmp;
    private readonly Rectangle _virtualPxBounds;
    private readonly WriteableBitmap _loupeBitmap = new(11, 11, 96, 96, PixelFormats.Bgra32, null);
    private readonly byte[] _loupePixels = new byte[11 * 11 * 4];

    public MediaColor PickedColor { get; private set; }
    public string PickedHex { get; private set; } = string.Empty;

    public ScreenColorPickerOverlay()
    {
        InitializeComponent();
        _virtualPxBounds = ScreenCaptureOcrService.GetVirtualBounds();
        _virtualBmp = ScreenCaptureOcrService.CaptureVirtualScreen();
        SnapshotImg.Source = ScreenCaptureOcrService.ToBitmapSource(_virtualBmp);
        LoupeImage.Source = _loupeBitmap;

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

        var primary = Forms.Screen.PrimaryScreen?.Bounds ?? _virtualPxBounds;
        double primaryCenterDipX = (primary.Left - _virtualPxBounds.Left + primary.Width / 2.0) * (ActualWidth / _virtualPxBounds.Width);
        double primaryTopDipY = (primary.Top - _virtualPxBounds.Top + 26) * (ActualHeight / _virtualPxBounds.Height);
        TopHintPill.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(TopHintPill, Math.Max(12, primaryCenterDipX - TopHintPill.DesiredSize.Width / 2.0));
        Canvas.SetTop(TopHintPill, Math.Max(12, primaryTopDipY));

        Activate();
        Focus();
    }

    private void OverlayCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pos = e.GetPosition(OverlayCanvas);
        UpdateLoupeAt(pos);
    }

    private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(OverlayCanvas);
        UpdateLoupeAt(pos);
        DialogResult = true;
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

    private void UpdateLoupeAt(WpfPoint dipPos)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        int px = Math.Clamp((int)Math.Round(dipPos.X * (_virtualBmp.Width / ActualWidth)), 0, _virtualBmp.Width - 1);
        int py = Math.Clamp((int)Math.Round(dipPos.Y * (_virtualBmp.Height / ActualHeight)), 0, _virtualBmp.Height - 1);

        var center = _virtualBmp.GetPixel(px, py);
        PickedColor = MediaColor.FromRgb(center.R, center.G, center.B);
        PickedHex = ScreenCaptureOcrService.ToHex(PickedColor);

        for (int dy = -5; dy <= 5; dy++)
        {
            int sy = Math.Clamp(py + dy, 0, _virtualBmp.Height - 1);
            for (int dx = -5; dx <= 5; dx++)
            {
                int sx = Math.Clamp(px + dx, 0, _virtualBmp.Width - 1);
                var c = _virtualBmp.GetPixel(sx, sy);
                int idx = ((dy + 5) * 11 + (dx + 5)) * 4;
                _loupePixels[idx] = c.B;
                _loupePixels[idx + 1] = c.G;
                _loupePixels[idx + 2] = c.R;
                _loupePixels[idx + 3] = 255;
            }
        }
        _loupeBitmap.WritePixels(new Int32Rect(0, 0, 11, 11), _loupePixels, 11 * 4, 0);

        ColorDot.Fill = new SolidColorBrush(PickedColor);
        HexText.Text = PickedHex;
        RgbText.Text = $"RGB({center.R}, {center.G}, {center.B})";

        LoupeCard.Visibility = Visibility.Visible;
        double lx = dipPos.X + 20;
        double ly = dipPos.Y + 20;
        if (lx + 130 > ActualWidth) lx = dipPos.X - 135;
        if (ly + 170 > ActualHeight) ly = dipPos.Y - 175;
        Canvas.SetLeft(LoupeCard, Math.Max(4, lx));
        Canvas.SetTop(LoupeCard, Math.Max(4, ly));
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _virtualBmp.Dispose();
    }
}
