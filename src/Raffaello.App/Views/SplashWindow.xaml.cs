using System.Windows;

namespace Raffaello.App.Views;

public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    public string Status
    {
        get => StatusText.Text;
        set => StatusText.Text = value;
    }
}
