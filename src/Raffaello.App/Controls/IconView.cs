using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Raffaello.App.Controls;

/// <summary>A line icon: path geometry on a 24-unit grid stroked in the current Foreground.</summary>
public sealed class IconView : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(IconView), new PropertyMetadata(null));

    public static readonly DependencyProperty StrokeWidthProperty =
        DependencyProperty.Register(nameof(StrokeWidth), typeof(double), typeof(IconView), new PropertyMetadata(2.33));

    static IconView()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(IconView), new FrameworkPropertyMetadata(typeof(IconView)));
    }

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    /// <summary>Stroke in 24-unit space; 2.33 renders as 1.75px at 18px.</summary>
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
}
