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
            RunScreenshot(e.Args[idx + 1], e.Args[idx + 2]);
            return;
        }

        base.OnStartup(e);
    }

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
            settings.Show();
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


