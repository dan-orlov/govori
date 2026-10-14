using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Govori;

public sealed class App : Application
{
    private TrayIcon? tray;
    internal DictationController? Controller { get; private set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            if (args.Contains("--paste-target"))
            {
                int index = Array.IndexOf(args, "--paste-target");
                desktop.MainWindow = Verification.CreatePasteTarget(args[index + 1], args[index + 2]);
                base.OnFrameworkInitializationCompleted(); return;
            }
            Controller = new DictationController();
            var window = new MainWindow(Controller); desktop.MainWindow = window;
            Controller.Attach(window);
            if (OperatingSystem.IsWindows() && !args.Contains("--verify") && !args.Contains("--preview"))
            {
                try { Controller.Platform.ConfigureStartup(Controller.Settings.StartWithSystem); }
                catch (Exception ex) { Controller.SetStatus(Localization.T("StartupError", ex.Message)); }
            }
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CreateTray(window, desktop);
            desktop.Exit += (_, _) => { tray?.Dispose(); Controller.Dispose(); };
            // Only the close button hides the window. Quit requests (Command+Q, Dock),
            // logout and shutdown also close the indicator windows, so they must exit.
            window.Closing += (_, e) => { if (!Controller.IsExiting && e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) && !args.Contains("--verify") && !args.Contains("--preview")) { e.Cancel = true; window.Hide(); } };
            if (!args.Contains("--verify") && !args.Contains("--preview"))
            {
                try { Controller.Platform.RequestPermissions(); }
                catch (Exception ex) { Controller.SetStatus(ex.Message); }
            }
            if (args.Contains("--tray"))
            {
                EventHandler? hideOnStartup = null;
                hideOnStartup = (_, _) =>
                {
                    window.Opened -= hideOnStartup;
                    Dispatcher.UIThread.Post(() => { if (!Program.ActivationRequested) window.Hide(); });
                };
                window.Opened += hideOnStartup;
            }
            bool verificationStarted = false;
            if (args.Contains("--verify")) window.Opened += async (_, _) =>
            {
                if (verificationStarted) return;
                verificationStarted = true;
                try { await Verification.RunAsync(window, Controller, args); desktop.Shutdown(0); }
                catch (Exception ex) { Verification.WriteFailure(ex); desktop.Shutdown(1); }
            };
            if (args.Contains("--preview")) window.Opened += async (_, _) =>
            {
                await Task.Delay(500); Verification.Render(window, Path.Combine(Configuration.DataDirectory, "preview.png")); desktop.Shutdown();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
    private void CreateTray(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var history = new NativeMenuItem(Localization.T("History")); history.Click += (_, _) => OpenFromTray();
        var settings = new NativeMenuItem(Localization.T("Settings")); settings.Click += (_, _) => OpenFromTray(settings: true);
        var pause = new NativeMenuItem(Localization.T("PauseHotkeys")) { ToggleType = NativeMenuItemToggleType.CheckBox };
        pause.Click += (_, _) => { Controller!.Hotkeys.Enabled = !Controller.Hotkeys.Enabled; pause.IsChecked = !Controller.Hotkeys.Enabled; };
        var exit = new NativeMenuItem(Localization.T("Exit")); exit.Click += async (_, _) => { await Controller!.ExitAsync(); desktop.Shutdown(); };
        var menu = new NativeMenu(); menu.Items.Add(history); menu.Items.Add(settings); menu.Items.Add(pause); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(exit);
        tray = new TrayIcon { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Govori.CrossPlatform/Assets/govori.ico"))), ToolTipText = Localization.T("TrayTooltip"), Menu = menu };
        void RefreshTrayLabels()
        {
            history.Header = Localization.T("History"); settings.Header = Localization.T("Settings");
            pause.Header = Localization.T("PauseHotkeys"); exit.Header = Localization.T("Exit"); tray.ToolTipText = Localization.T("TrayTooltip");
        }
        Localization.Changed += RefreshTrayLabels;
        desktop.Exit += (_, _) => Localization.Changed -= RefreshTrayLabels;
        tray.Clicked += (_, _) => OpenFromTray();
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }
    internal void OpenFromTray(bool settings = false) => Dispatcher.UIThread.Post(() =>
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
        {
            if (settings) window.OpenSettings(); else window.OpenHistory();
        }
    });
}
