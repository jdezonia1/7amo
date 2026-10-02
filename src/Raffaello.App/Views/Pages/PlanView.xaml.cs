using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views.Pages;

public partial class PlanView : UserControl
{
    private Point? _dragFrom;
    private Point _scrollFrom;
    private bool _dragged;

    public PlanView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(Fit, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Fit()
    {
        if (PlanHost.Width <= 0 || Scroller.ViewportWidth <= 0) return;
        var k = Math.Min(Scroller.ViewportWidth / PlanHost.Width, Scroller.ViewportHeight / Math.Max(1, PlanHost.Height));
        Zoom.ScaleX = Zoom.ScaleY = Math.Clamp(k, 0.05, 8);
    }

    private void OnFit(object sender, RoutedEventArgs e) => Fit();

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var old = Zoom.ScaleX;
        var k = Math.Clamp(old * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.05, 8);
        var mouse = e.GetPosition(Scroller);
        var contentX = (Scroller.HorizontalOffset + mouse.X) / old;
        var contentY = (Scroller.VerticalOffset + mouse.Y) / old;
        Zoom.ScaleX = Zoom.ScaleY = k;
        Scroller.UpdateLayout();
        Scroller.ScrollToHorizontalOffset(contentX * k - mouse.X);
        Scroller.ScrollToVerticalOffset(contentY * k - mouse.Y);
        e.Handled = true;
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(Scroller);
        _scrollFrom = new Point(Scroller.HorizontalOffset, Scroller.VerticalOffset);
        _dragged = false;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Scroller);
        var d = p - from;
        if (!_dragged && Math.Abs(d.X) + Math.Abs(d.Y) < 6) return;
        _dragged = true;
        Scroller.ScrollToHorizontalOffset(_scrollFrom.X - d.X);
        Scroller.ScrollToVerticalOffset(_scrollFrom.Y - d.Y);
        Scroller.Cursor = Cursors.SizeAll;
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null;
        Scroller.Cursor = null;
    }

    private void OnRoomClick(object sender, MouseButtonEventArgs e)
    {
        if (_dragged) return;
        if (sender is FrameworkElement { Tag: string room } && DataContext is PlanViewModel vm) vm.SelectRoom(room);
    }

    private void OnExportPng(object sender, RoutedEventArgs e)
    {
        var d = new SaveFileDialog { Title = "Export plan view", Filter = "PNG image|*.png", FileName = $"PLAN_{(DataContext as PlanViewModel)?.Level?.Plan.Plan}_{DateTime.Now:yyyyMMdd_HHmm}.png" };
        if (d.ShowDialog() != true) return;
        var w = (int)Math.Ceiling(PlanHost.ActualWidth);
        var h = (int)Math.Ceiling(PlanHost.ActualHeight);
        if (w <= 0 || h <= 0) return;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(PlanHost) { Stretch = Stretch.None }, null, new Rect(0, 0, w, h));
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(d.FileName);
        enc.Save(fs);
    }
}
