using Microsoft.UI.Xaml;

namespace CXAudioOutput;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    public static LaunchOptions Options { get; } = LaunchOptions.Parse(
        Environment.GetCommandLineArgs().Skip(1).ToArray());

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(Options);
        _window.Activate();
    }
}
