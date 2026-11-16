using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ThixNote.App.ViewModels;
using ThixNote.App.Views;

namespace ThixNote.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = viewModel.InitialWindowWidth,
                Height = viewModel.InitialWindowHeight,
            };
            desktop.MainWindow = window;
            viewModel.OpenFiles(Program.StartupFiles);

            SingleInstance.StartListening(files => Dispatcher.UIThread.Post(() => OpenFromOtherInstance(window, viewModel, files)));
            ListenForMacFileActivation(window, viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OpenFromOtherInstance(Window window, MainViewModel viewModel, IReadOnlyList<string> files)
    {
        viewModel.OpenFiles(files);
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>On macOS, Finder delivers opened documents as an activation event instead of command line arguments.</summary>
    private void ListenForMacFileActivation(Window window, MainViewModel viewModel)
    {
        if (TryGetFeature(typeof(IActivatableLifetime)) is not IActivatableLifetime activatable)
        {
            return;
        }

        activatable.Activated += (_, eventArgs) =>
        {
            if (eventArgs is not FileActivatedEventArgs fileArgs)
            {
                return;
            }

            var paths = fileArgs.Files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
            Dispatcher.UIThread.Post(() => OpenFromOtherInstance(window, viewModel, paths));
        };
    }
}
