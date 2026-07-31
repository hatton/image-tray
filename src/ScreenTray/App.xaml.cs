using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.Win32;
using ScreenTray.Models;
using ScreenTray.Services;
using ScreenTray.ViewModels;

namespace ScreenTray;

/// <summary>
/// Startup wiring, the tray icon, and the app's lifetime.
/// </summary>
/// <remarks>
/// ShutdownMode is OnExplicitShutdown, so hiding the only window does not end the
/// process. That is deliberate: the whole point of the tray icon is that closing
/// the strip and getting it back should cost one click, and a process that had to
/// cold-start each time could not manage that.
/// </remarks>
public partial class App : Application
{
    private const int SM_CXSMICON = 49;

    private SingleInstance? _singleInstance;
    private SettingsStore? _store;
    private AppSettings? _settings;
    private TrayViewModel? _viewModel;
    private MainWindow? _window;
    private TaskbarIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        var fromLogin = e.Args.Any(arg =>
            string.Equals(arg, AutoStartService.AutoStartArgument, StringComparison.OrdinalIgnoreCase));

        // A second launch surfaces the instance already running rather than adding
        // a competing tray icon.
        _singleInstance = SingleInstance.Claim(() => Dispatcher.InvokeAsync(ShowWindow));
        if (_singleInstance is null)
        {
            Log.Info("Another instance is already running; asked it to show itself.");
            Shutdown();
            return;
        }

        _store = new SettingsStore();
        _settings = _store.Load();

        ThemeService.Apply(_settings.ThemeMode);
        SyncAutoStart();

        _viewModel = new TrayViewModel(_store, _settings)
        {
            RequestFolderChoice = PickFolder,
            ConfirmBulkRotation = ConfirmBulkRotation,
        };

        // A login launch that is going to stay in the tray must not throw a folder
        // chooser at the user while they are still signing in. The strip shows its
        // empty state instead, with a button, whenever they do open it.
        var startHidden = fromLogin && _settings.ClosedToTray;

        if (!startHidden && !EnsureFolderChosen())
        {
            Shutdown();
            return;
        }

        _window = new MainWindow(_viewModel);
        _viewModel.NewScreenshotArrived += (_, _) => _window?.ShowForNewScreenshot();
        _viewModel.Start();

        CreateTrayIcon();

        if (startHidden)
        {
            Log.Info("Signed in with the strip closed last time, so staying in the tray.");
        }
        else
        {
            ShowWindow();
        }
    }

    /// <summary>
    /// Brings the Run key into line with the setting. Autostart defaults to on, so
    /// this is what actually registers it on a first run, and it also repairs the
    /// entry after the exe has been moved.
    /// </summary>
    private void SyncAutoStart()
    {
        if (_settings!.RunAtLogin)
        {
            if (!AutoStartService.IsCurrent())
            {
                AutoStartService.SetEnabled(true);
            }

            return;
        }

        if (AutoStartService.IsEnabled())
        {
            AutoStartService.SetEnabled(false);
        }
    }

    /// <summary>
    /// Makes sure there is a usable folder before the window appears.
    /// </summary>
    /// <returns>
    /// False only when this is a first run and the chooser was cancelled, since
    /// there is then nothing whatsoever to show. A previously chosen folder that
    /// has since vanished leaves the app running with its empty state, which is
    /// recoverable without restarting.
    /// </returns>
    private bool EnsureFolderChosen()
    {
        var folder = _settings!.WatchedFolder;
        var neverChosen = string.IsNullOrWhiteSpace(folder);

        if (!neverChosen && Directory.Exists(folder))
        {
            return true;
        }

        if (!neverChosen)
        {
            Log.Info($"The watched folder '{folder}' is missing; asking for another.");
        }

        var chosen = PickFolder(neverChosen ? null : folder);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            _settings.WatchedFolder = chosen;
            _store!.Save(_settings);
            return true;
        }

        return !neverChosen;
    }

    private string? PickFolder(string? current)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder your screenshots are saved into",
            Multiselect = false,
            InitialDirectory = ResolveInitialDirectory(current),
        };

        var owner = _window is { IsVisible: true } ? _window : null;
        var chose = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);

        return chose == true ? dialog.FolderName : null;
    }

    private static string ResolveInitialDirectory(string? current)
    {
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            return current;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    /// <summary>
    /// Asked once per folder, and only when the first pass would remove more than a
    /// couple of files. Pointing the app at a folder that already holds hundreds of
    /// images should not quietly empty most of it, even into the Recycle Bin.
    /// </summary>
    private bool ConfirmBulkRotation(string folder, int count)
    {
        var keep = _settings!.NormalisedKeepCount();

        var message =
            $"Screen Tray keeps the newest {keep} screenshots in:\n\n{folder}\n\n" +
            $"There are {count} older image files there. Send those {count} to the Recycle Bin?\n\n" +
            "They stay recoverable from the Recycle Bin, and this folder will be kept trimmed from now on.";

        var owner = _window is { IsVisible: true } ? _window : null;

        var answer = owner is null
            ? MessageBox.Show(message, "Screen Tray", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, message, "Screen Tray", MessageBoxButton.YesNo, MessageBoxImage.Question);

        return answer == MessageBoxResult.Yes;
    }

    private void CreateTrayIcon()
    {
        var menu = new ContextMenu();
        menu.Items.Add(BuildMenuItem("Show Screen Tray", ShowWindow));
        menu.Items.Add(BuildMenuItem("Settings…", OpenSettings));
        menu.Items.Add(BuildMenuItem("Choose folder…", () => _viewModel!.ChooseFolder()));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildMenuItem("Exit Screen Tray", QuitApp));

        _trayIcon = new TaskbarIcon
        {
            IconSource = LoadTrayIcon(),
            ToolTipText = "Screen Tray — click to see your recent screenshots",

            // Without this, a left click waits to find out whether it is really a
            // double click, which makes the one gesture that matters feel sluggish.
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(ShowWindow),
            ContextMenu = menu,
        };

        // Efficiency mode lowers the process priority while no window is showing.
        // Declined on purpose: this app has to react promptly to a new file even
        // when it is hidden.
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    private static MenuItem BuildMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Loads the tray icon at the exact size Windows asks for, so the .ico's
    /// dedicated small frame gets used instead of a downscaled large one.
    /// </summary>
    private static ImageSource LoadTrayIcon()
    {
        var size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/app.ico");
        image.DecodePixelWidth = size;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.ShowAndActivate();
    }

    private void OpenSettings()
    {
        ShowWindow();

        if (_window is null || _viewModel is null)
        {
            return;
        }

        var dialog = new SettingsWindow(_viewModel) { Owner = _window };
        dialog.ShowDialog();
    }

    private void QuitApp()
    {
        _window?.SavePlacement();
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception on the UI thread.", e.Exception);

        MessageBox.Show(
            $"Screen Tray hit a problem:\n\n{e.Exception.Message}\n\nIt will try to carry on. Details are in the log next to the settings file.",
            "Screen Tray",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        // Better a slightly wounded tray than a vanished one.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _viewModel?.Dispose();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);
}
