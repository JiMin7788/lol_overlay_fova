using System.Diagnostics;
using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Overlay.Client;

/// <summary>
/// Tray presence while "start with League" is on (see <see cref="Autostart"/>): an icon with
/// Open / Exit, and a watch on the League client process that shows the dashboard each time the
/// client starts while the dashboard is hidden. Showing is done without activation, so a client
/// that is still launching keeps focus. Process names only — no access to the process itself.
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private const string LeagueClientProcess = "LeagueClientUx";

    private readonly Forms.NotifyIcon _icon;
    private readonly DispatcherTimer _watch;
    private readonly Func<bool> _isDashboardVisible;
    private readonly Action _showForClient;
    private bool _clientWasRunning;

    public TrayHost(Func<bool> isDashboardVisible, Action showForClient, Action open, Action exit)
    {
        _isDashboardVisible = isDashboardVisible;
        _showForClient = showForClient;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Localization.L("tray.open"), null, (_, _) => open());
        menu.Items.Add(Localization.L("tray.exit"), null, (_, _) => exit());
        _icon = new Forms.NotifyIcon
        {
            Text = "Fova",
            Icon = Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) : SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();

        // Count a client already running at start as seen: the dashboard is shown or hidden by
        // the start mode, and only a client that starts LATER should raise it.
        _clientWasRunning = IsClientRunning();
        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _watch.Tick += (_, _) => Poll();
        _watch.Start();
    }

    /// <summary>True when Fova was started hidden and the client is already up (sign-in raced
    /// the League auto-launch), so the caller shows the dashboard right away.</summary>
    public bool ClientAlreadyRunning => _clientWasRunning;

    /// <summary>Windows notification from the tray icon (used once, when closing the window
    /// first hides Fova to the tray instead of exiting).</summary>
    public void Notify(string title, string text)
        => _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    private void Poll()
    {
        bool running = IsClientRunning();
        if (running && !_clientWasRunning && !_isDashboardVisible()) _showForClient();
        _clientWasRunning = running;
    }

    private static bool IsClientRunning()
    {
        var found = Process.GetProcessesByName(LeagueClientProcess);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    public void Dispose()
    {
        _watch.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
