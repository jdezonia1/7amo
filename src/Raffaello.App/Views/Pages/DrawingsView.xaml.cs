using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views.Pages;

/// <summary>[drawings] Mouse handling of the review canvas: wheel = zoom, right drag = pan, click / drag by mode.</summary>
public partial class DrawingsView : UserControl
{
    private Point? _down;
    private Point? _panFrom;

    public DrawingsView()
    {
        InitializeComponent();
        Host.MouseRightButtonDown += (_, e) => { _panFrom = e.GetPosition(Scroller); Host.CaptureMouse(); };
        Host.MouseRightButtonUp += (_, _) => { _panFrom = null; Host.ReleaseMouseCapture(); };
    }

    private DrawingsViewModel? Vm => DataContext as DrawingsViewModel;

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None && Keyboard.Modifiers != ModifierKeys.Control) return;
        var k = e.Delta > 0 ? 1.2 : 1 / 1.2;
        Zoom.ScaleX = Zoom.ScaleY = Math.Clamp(Zoom.ScaleX * k, 0.05, 6);
        e.Handled = true;
    }

    private void OnFit(object sender, RoutedEventArgs e)
    {
        if (Vm is null || Vm.ImageWidth <= 0) return;
        var k = Math.Min(Scroller.ActualWidth / Vm.ImageWidth, Scroller.ActualHeight / Vm.ImageHeight) * 0.98;
        Zoom.ScaleX = Zoom.ScaleY = Math.Max(0.02, k);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _down = e.GetPosition(Host);
        if (Vm?.Mode == "DEFINE SYMBOL")
        {
            Rubber.Visibility = Visibility.Visible;
            Canvas.SetLeft(Rubber, _down.Value.X); Canvas.SetTop(Rubber, _down.Value.Y);
            Rubber.Width = Rubber.Height = 0;
            Host.CaptureMouse();
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_panFrom is { } from && e.RightButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(Scroller);
            Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - (p.X - from.X));
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - (p.Y - from.Y));
            _panFrom = p;
            return;
        }
        if (_down is not { } d || Rubber.Visibility != Visibility.Visible) return;
        var q = e.GetPosition(Host);
        Canvas.SetLeft(Rubber, Math.Min(d.X, q.X)); Canvas.SetTop(Rubber, Math.Min(d.Y, q.Y));
        Rubber.Width = Math.Abs(q.X - d.X); Rubber.Height = Math.Abs(q.Y - d.Y);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_down is not { } d || Vm is null) return;
        var q = e.GetPosition(Host);
        _down = null;
        if (Rubber.Visibility == Visibility.Visible)
        {
            Rubber.Visibility = Visibility.Collapsed;
            Host.ReleaseMouseCapture();
            if (Math.Abs(q.X - d.X) > 3 && Math.Abs(q.Y - d.Y) > 3) Vm.OnBoxDrawn(d.X, d.Y, q.X, q.Y);
            return;
        }
        var marker = (e.OriginalSource as FrameworkElement)?.DataContext as HitMarker;
        Vm.OnCanvasClick(q.X, q.Y, marker);
    }

    private void OnPlanClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || PlanImg.Source is not BitmapSource || PlanImg.ActualWidth <= 0) return;
        var p = e.GetPosition(PlanImg);
        Vm.OnPlanClick(p.X / PlanImg.ActualWidth, p.Y / PlanImg.ActualHeight);
    }
}
