using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AkashicRecords.Infrastructure.Configuration;

namespace AkashicRecords.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // The WPF MediaPlayer gates https media behind a zone check (default off): internet
        // URLs fail with "Only site-of-origin pack URIs are supported". iTunes preview clips
        // are https, so restore the legacy allow-all behavior for this app.
        AppContext.SetSwitch("Switch.System.Windows.Net.DoNotApplyZoneCheckForDefaultCredentials", true);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AltTabHider.RegisterForAllWindows();
    }

    // Headless capture: `--screenshot <Section>[:<SubTab>] <outputPath>` opens the given view,
    // renders it to a PNG and exits. Lets an automated visual check see the real UI without
    // driving mouse clicks. Example: --screenshot Journaux:Personal D:\shot.png
    protected override void OnStartup(StartupEventArgs e)
    {
        var idx = Array.IndexOf(e.Args, "--screenshot");
        if (idx >= 0 && idx + 2 <= e.Args.Length - 1)
        {
            // Skip base.OnStartup so StartupUri (the normal window) is never navigated to.
            Headless = true;
            RunScreenshot(e.Args[idx + 1], e.Args[idx + 2]);
            return;
        }

        // Headless sync: `--sync-once <reportPath>` runs one synchronisation with the hub this computer
        // is paired with, writes what happened to the given file and exits. For scripts and checks;
        // like the captures above it runs beside the normal app.
        var syncIdx = Array.IndexOf(e.Args, "--sync-once");
        if (syncIdx >= 0 && syncIdx + 1 <= e.Args.Length - 1)
        {
            var sync = Sync.SyncService.Instance;
            // Off the dispatcher thread: nothing here pumps messages for an awaited continuation.
            var report = Task.Run(() => sync.RunAsync()).GetAwaiter().GetResult();
            File.WriteAllText(e.Args[syncIdx + 1],
                !sync.Settings.IsPaired ? "non appaire" : sync.LastError is not null ? "ERREUR: " + sync.LastError : "OK: " + report);
            Shutdown(sync.LastError is null && sync.Settings.IsPaired ? 0 : 1);
            return;
        }

        // Headless computing: `--compute-once <reportPath>` asks the hub for one job (see ComputeService),
        // does it if one comes, writes what happened and exits. Runs beside the normal app, like the above.
        var computeIdx = Array.IndexOf(e.Args, "--compute-once");
        if (computeIdx >= 0 && computeIdx + 1 <= e.Args.Length - 1)
        {
            var result = Task.Run(() => Sync.ComputeService.Instance.RunOnceAsync()).GetAwaiter().GetResult();
            File.WriteAllText(e.Args[computeIdx + 1], result);
            Shutdown(result.StartsWith("OK") ? 0 : 1);
            return;
        }

        // One running app per Windows session. A second one (« Start with Windows » plus a launch by
        // hand) could not register the global hotkey, ended up with no tray icon — so no way to quit
        // it — and wrote the same config.json and database. It simply leaves; headless captures above
        // are not concerned and still run beside the app.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\AkashicRecords.App.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>True for a capture run: background work that talks to the outside (the sync) stays off.</summary>
    public static bool Headless { get; private set; }

    // Held for the life of the process (a collected Mutex would release the name).
    private static Mutex? _singleInstance;

    private void RunScreenshot(string target, string outputPath)
    {
        var parts = target.Split(':', 2);
        var section = parts[0];
        var subTab = parts.Length > 1 ? parts[1] : null;

        // The settings window is a top-level Window, not a section hosted inside MainWindow, so it
        // renders itself and is captured whole (the generic path below only renders MainWindow.Content).
        if (section.Equals("Settings", StringComparison.OrdinalIgnoreCase))
        {
            var settings = new Views.SettingsView(new ConfigService().Load(), new ConfigService());
            settings.WindowStartupLocation = WindowStartupLocation.Manual;
            settings.Left = 0;
            settings.Top = 0;
            // Sized to its card, or the capture frames the top-left of a much larger empty window.
            settings.SizeToContent = SizeToContent.WidthAndHeight;
            settings.Show();
            // `Settings:Sync` scrolls to the synchronisation block, which is below the fold.
            if (string.Equals(subTab, "Sync", StringComparison.OrdinalIgnoreCase)) settings.ScrollToSyncForScreenshot();
            var settingsSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            settingsSettle.Tick += (_, _) =>
            {
                settingsSettle.Stop();
                try { CaptureElement((FrameworkElement)settings.Content, outputPath); }
                catch (Exception ex) { LogCrash(ex); }
                finally { Shutdown(); }
            };
            settingsSettle.Start();
            return;
        }

        // The shared observations panel, pointed at one item: `Observations:<OwnerKind>:<Id>`
        // (e.g. Observations:Poem:7). Its dialog is modal, so the panel is rendered in a plain window.
        if (section.Equals("Observations", StringComparison.OrdinalIgnoreCase))
        {
            var owner = (subTab ?? string.Empty).Split(':', 2);
            var panel = new Views.ObservationsPanel { Width = 440, Margin = new Thickness(20) };
            panel.SetOwner(owner[0], owner.Length > 1 && int.TryParse(owner[1], out var ownerId) ? ownerId : 0);
            var host = new Window
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
                SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                Background = (System.Windows.Media.Brush)FindResource("AppBgBrush"), Content = panel,
            };
            host.Show();
            var panelSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            panelSettle.Tick += (_, _) =>
            {
                panelSettle.Stop();
                try { CaptureElement(panel, outputPath); }
                catch (Exception ex) { LogCrash(ex); }
                finally { host.Close(); Shutdown(); }
            };
            panelSettle.Start();
            return;
        }

        // The music player is also a standalone top-level window (not hosted in MainWindow).
        if (section.Equals("Music", StringComparison.OrdinalIgnoreCase) ||
            section.Equals("Musique", StringComparison.OrdinalIgnoreCase))
        {
            var player = new MusicPlayerWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0,
                Top = 0
            };
            player.Show();
            // Open the expand panel so the library rows (and the ∞ chain buttons) are visible.
            player.ExpandedPanel.Visibility = Visibility.Visible;
            player.ExpandButton.Content = "▴";
            player.SizeToContent = SizeToContent.Manual;
            player.Height = 560;
            var playerSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            playerSettle.Tick += (_, _) =>
            {
                playerSettle.Stop();
                try { CaptureElement((FrameworkElement)player.Content, outputPath); }
                catch (Exception ex) { LogCrash(ex); }
                finally { player.Close(); Shutdown(); }
            };
            playerSettle.Start();
            return;
        }

        // Floating save pill, standalone window; sub-tab picks the state (None/Saving/Saved).
        if (section.Equals("SaveWidget", StringComparison.OrdinalIgnoreCase))
        {
            var widget = new MusicSaveWidget { Left = 0, Top = 0 };
            var state = Enum.TryParse<TrackSaveState>(subTab, true, out var s) ? s : TrackSaveState.None;
            widget.Render(state);
            widget.Show();
            var widgetSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            widgetSettle.Tick += (_, _) =>
            {
                widgetSettle.Stop();
                try { CaptureElement((FrameworkElement)widget.Content, outputPath); }
                catch (Exception ex) { LogCrash(ex); }
                finally { widget.ForceClose(); Shutdown(); }
            };
            widgetSettle.Start();
            return;
        }

        // Same reasoning for the calendar toast: standalone window, fed with the real agenda so the
        // capture shows what the user would actually see today.
        if (section.Equals("CalendarToast", StringComparison.OrdinalIgnoreCase))
        {
            var service = new AkashicRecords.Infrastructure.Persistence.CalendarService(
                new AkashicRecords.Infrastructure.Persistence.SqliteConnectionFactory());
            var todays = service.GetRange(DateTime.Today, DateTime.Today);
            var toast = new CalendarToast
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0,
                Top = 0,
            };
            toast.SetItems("Aujourd'hui", todays.Select(i => new CalendarToast.ToastLine(
                Views.CalendarView.ColorFor(i), i.TimeText, i.Title, i.Subtitle, i.NotificationKey)).ToList());
            toast.Show();
            var toastSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            toastSettle.Tick += (_, _) =>
            {
                toastSettle.Stop();
                try { CaptureElement((FrameworkElement)toast.Content, outputPath); }
                catch (Exception ex) { LogCrash(ex); }
                finally { toast.ForceClose(); Shutdown(); }
            };
            toastSettle.Start();
            return;
        }

        // The hub's download board (needs this copy paired with a hub: config\sync.json).
        if (section == "Telechargements")
        {
            var board = new HubDownloadsWindow { Left = 0, Top = 0 };
            board.Show();
            var boardSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
            boardSettle.Tick += (_, _) =>
            {
                boardSettle.Stop();
                try { CaptureElement((FrameworkElement)board.Content, outputPath); }
                catch (Exception ex) { LogCrash(ex); }
                finally { board.ForceClose(); Shutdown(); }
            };
            boardSettle.Start();
            return;
        }

        // The image upscaler (needs this copy paired with a hub: config\sync.json).
        if (section == "Agrandir")
        {
            var upscale = new UpscaleWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            upscale.Show();
            var upscaleSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4000) };
            upscaleSettle.Tick += (_, _) =>
            {
                upscaleSettle.Stop();
                try { CaptureElement((FrameworkElement)upscale.Content, outputPath); }
                catch (Exception ex) { LogCrash(ex); }
                finally { upscale.ForceClose(); Shutdown(); }
            };
            upscaleSettle.Start();
            return;
        }

        var window = new MainWindow(screenshotMode: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0
        };
        window.Show();
        window.OpenSectionForScreenshot(section, subTab);

        // Give layout + repository loads time to settle before rendering, then capture and exit.
        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            try
            {
                // The calendar link picker renders in a detached popup visual (its own PopupRoot),
                // not inside the window's content tree — RenderTargetBitmap on the window would miss
                // it. The popup only opens once the view's Loaded has run, so resolve it here, after
                // the settle delay, not synchronously after OpenSectionForScreenshot.
                var target = section == "Calendrier" && subTab == "Picker"
                    ? window.CalendarPickerElementForScreenshot() ?? (FrameworkElement)window.Content
                    : section == "Calendrier" && subTab == "Hover"
                        ? window.CalendarPreviewCardForScreenshot() ?? (FrameworkElement)window.Content
                        : (FrameworkElement)window.Content;
                CaptureElement(target, outputPath);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
            }
            finally
            {
                Shutdown();
            }
        };
        settle.Start();
    }

    private static void CaptureElement(FrameworkElement element, string outputPath)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        if (width <= 0 || height <= 0) { width = 1920; height = 1080; }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogCrash(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString()));
    }

    private static void LogCrash(Exception exception)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AkashicRecords");
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "crash.log"), $"{DateTime.Now:O}\n{exception}\n\n");
    }
}


