using System.Windows;
using System.Windows.Controls;

namespace Raffaello.App.Controls;

public partial class LogoMark : UserControl
{
    public static readonly DependencyProperty ShowTileProperty =
        DependencyProperty.Register(nameof(ShowTile), typeof(bool), typeof(LogoMark), new PropertyMetadata(true));

    public LogoMark() => InitializeComponent();

    /// <summary>Draw the black rounded tile behind the links (off when placed on the black sidebar).</summary>
    public bool ShowTile { get => (bool)GetValue(ShowTileProperty); set => SetValue(ShowTileProperty, value); }
}
