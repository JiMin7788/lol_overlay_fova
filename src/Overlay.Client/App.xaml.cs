using System;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Overlay.Client;

/// <summary>
/// Interaction logic for App.xaml.
///
/// Installs GLOBAL unhandled-exception handlers so a startup/overlay crash is never a SILENT
/// immediate exit (the app previously vanished with no trace when any exception escaped the
/// UI thread, a background thread, or a faulted Task). Every unhandled exception is now:
///   1. appended to <c>logs/crash-YYYY-MM-DD.log</c> next to the executable (full ToString),
///   2. surfaced in a MessageBox so the user can see and report the cause.
/// UI-thread (Dispatcher) exceptions are marked handled AFTER logging so the app stays alive
/// when it safely can; process-level faults are logged best-effort on the way down.
/// The handlers are wrapped so the crash reporter itself can never throw.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // App-wide hover-help timing (2026-07-25 request): every tooltip appears after 0.3s and
        // stays long enough to read. Metadata override so no per-element wiring is needed.
        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(DependencyObject), new FrameworkPropertyMetadata(300));
        System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(
            typeof(DependencyObject), new FrameworkPropertyMetadata(15000));

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report("UI-thread (Dispatcher)", e.Exception, showDialog: true);
        // Keep the app alive for a recoverable UI-thread fault instead of vanishing.
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => Report("AppDomain", e.ExceptionObject as Exception, showDialog: true);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Report("Unobserved Task", e.Exception, showDialog: false);
        e.SetObserved();
    }

    /// <summary>Best-effort crash reporter — logs to file and (optionally) shows a dialog.
    /// Never throws: a failure inside the reporter must not mask the original crash.
    /// Internal so deliberate, otherwise-silent failure sites (e.g. HomeWindow.ShowOverlay, where a
    /// corrupt overlay-config.json used to vanish into Debug.WriteLine) can route through the same
    /// file log + dialog surface the global handlers use.</summary>
    internal static void Report(string source, Exception? ex, bool showDialog)
    {
        string text = ex?.ToString() ?? "(null exception object)";
        try
        {
            // Same location as Overlay.Core's Logger.DefaultLogDirectory (logs/ next to the exe).
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir,
                "crash-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
            File.AppendAllText(file,
                "[" + DateTime.Now.ToString("O", CultureInfo.InvariantCulture) + "] UNHANDLED (" + source + ")\n"
                + text + "\n\n");
        }
        catch
        {
            // Swallow — the reporter must never throw; the dialog below is the fallback surface.
        }

        if (!showDialog) return;
        try
        {
            MessageBox.Show(
                "오버레이에서 처리되지 않은 오류가 발생했습니다 (" + source + ").\n\n"
                + text + "\n\n자세한 내용은 logs/crash-*.log 에 기록되었습니다.",
                "Overlay 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // No UI available (e.g. very early startup) — the file log above is the record.
        }
    }
}
