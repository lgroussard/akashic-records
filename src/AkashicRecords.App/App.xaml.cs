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
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
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
                var dbg = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AkashicRecords", "screenshot-debug.log");
                System.IO.File.AppendAllText(dbg, $"{DateTime.Now:O} settle tick. Content={window.Content?.GetType().Name ?? "null"}, W={window.ActualWidth}, H={window.ActualHeight}\n");
                CaptureElement((FrameworkElement)window.Content, outputPath);
                System.IO.File.AppendAllText(dbg, $"{DateTime.Now:O} CaptureElement done\n");
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


