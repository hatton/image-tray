using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScreenTray.Models;
using ScreenTray.Services;
using ScreenTray.ViewModels;

namespace ScreenTray;

/// <summary>
/// Settings apply as you change them, with a single Done to dismiss. For four
/// options, an OK/Cancel pair would only add a way to lose your changes.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// How long the keep-count slider has to sit still before it takes effect.
    /// </summary>
    /// <remarks>
    /// This is not cosmetic. Lowering the keep count recycles screenshots, and a
    /// slider reports every value it passes through, so dragging from 20 down to 4
    /// would recycle at 19, 18, 17 and so on all the way down. Dragging back up
    /// cannot undo any of it. Waiting for the drag to settle means only the value
    /// you actually chose ever gets acted on.
    /// </remarks>
    private static readonly TimeSpan KeepCountSettleDelay = TimeSpan.FromMilliseconds(700);

    private readonly TrayViewModel _viewModel;
    private readonly DispatcherTimer _keepCountSettle;
    private bool _loading = true;
    private int _pendingKeepCount;

    public SettingsWindow(TrayViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        _keepCountSettle = new DispatcherTimer { Interval = KeepCountSettleDelay };
        _keepCountSettle.Tick += (_, _) => CommitKeepCount();

        var settings = viewModel.Settings;

        FolderText.Text = settings.WatchedFolder ?? "No folder chosen yet";
        KeepSlider.Value = settings.NormalisedKeepCount();
        KeepText.Text = settings.NormalisedKeepCount().ToString();
        // The setting is what the checkbox reflects; startup reconciles the Run key
        // to match it, so the two cannot drift for long.
        AutoStartCheck.IsChecked = settings.RunAtLogin;
        PopUpCheck.IsChecked = settings.ShowOnNewScreenshot;

        CellAspectSlider.Value = TileMetrics.ClampCellAspect(settings.CellAspect);
        UpdateCellAspectText();

        ThemeCombo.SelectedIndex = settings.ThemeMode switch
        {
            AppThemeMode.Light => 1,
            AppThemeMode.Dark => 2,
            _ => 0,
        };

        Loaded += (_, _) => ThemeService.ApplyWindowFrame(this);
        _loading = false;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ChooseFolder();
        FolderText.Text = _viewModel.Settings.WatchedFolder ?? "No folder chosen yet";
    }

    private void OnKeepCountChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Minimum="4" in the XAML coerces Value up from 0 while the window is still
        // being parsed, so this fires before the parser has reached KeepText. Every
        // handler here has to tolerate being called mid-construction.
        if (!IsInitialized)
        {
            return;
        }

        var value = (int)Math.Round(e.NewValue);
        KeepText.Text = value.ToString();

        if (_loading)
        {
            return;
        }

        // The number updates as you drag, but nothing is recycled until you stop.
        _pendingKeepCount = value;
        _keepCountSettle.Stop();
        _keepCountSettle.Start();
    }

    private void CommitKeepCount()
    {
        _keepCountSettle.Stop();

        if (_pendingKeepCount == 0 || _pendingKeepCount == _viewModel.Settings.KeepCount)
        {
            return;
        }

        _viewModel.Settings.KeepCount = _pendingKeepCount;
        _viewModel.ApplyChangedSettings();
    }

    /// <summary>
    /// Closing the window while the slider is still settling should still honour the
    /// value you left it on.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        if (_keepCountSettle.IsEnabled)
        {
            CommitKeepCount();
        }

        base.OnClosed(e);
    }

    private void OnCellAspectChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateCellAspectText();

        if (_loading)
        {
            return;
        }

        // Live, unlike the keep count: this only changes layout, so there is nothing
        // destructive to wait for.
        _viewModel.ApplyCellAspect(CellAspectSlider.Value);
    }

    private void UpdateCellAspectText() => CellAspectText.Text = $"{CellAspectSlider.Value:0.##}×";

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _loading || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        if (!Enum.TryParse<AppThemeMode>(tag, out var mode))
        {
            return;
        }

        _viewModel.Settings.ThemeMode = mode;
        _viewModel.SaveSettings();
        ThemeService.Apply(mode);
        ThemeService.ApplyWindowFrame(this);
    }

    private void OnShowOnNewScreenshotChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _loading)
        {
            return;
        }

        _viewModel.Settings.ShowOnNewScreenshot = PopUpCheck.IsChecked == true;
        _viewModel.SaveSettings();
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _loading)
        {
            return;
        }

        var enabled = AutoStartCheck.IsChecked == true;
        AutoStartService.SetEnabled(enabled);

        _viewModel.Settings.RunAtLogin = enabled;
        _viewModel.SaveSettings();
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => Close();
}
