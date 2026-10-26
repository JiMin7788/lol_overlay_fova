using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Overlay.Client.UiTests;

/// <summary>
/// Real mouse and keyboard input (FlaUI, SendInput) that can only reach the session's own windows
/// (spec R6). Every mouse point is checked before the cursor moves; keys go out only while a session
/// window is in the foreground. Fova itself never sends input: only this test process does.
/// </summary>
public sealed class SafeInput
{
    private readonly FovaSession _session;

    internal SafeInput(FovaSession session) => _session = session;

    /// <summary>Throws without moving the mouse unless <paramref name="p"/> lies inside a visible
    /// top-level window of the session process.</summary>
    public void Guard(Point p)
    {
        if (!IsInsideSessionWindow(p))
            throw new InvalidOperationException($"Refused to move the mouse outside Fova's windows: ({p.X},{p.Y})");
    }

    public bool IsInsideSessionWindow(Point p)
    {
        if (_session.Process is not { HasExited: false } process) return false;
        bool inside = false;
        foreach (var hwnd in NativeMethods.VisibleTopLevelWindows(process.Id))
            if (NativeMethods.GetWindowRect(hwnd, out var rect) && rect.Contains(p.X, p.Y))
            {
                inside = true;
                break;
            }
        if (!inside) return false;
        // AI-NOTE: a window of another program stacked above Fova at this point would take the click,
        // so the window actually under the point must be Fova's too. Click-through (WS_EX_TRANSPARENT)
        // overlay windows are skipped by WindowFromPoint; HOME or a Fova popup beneath still counts.
        var under = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = p.X, Y = p.Y });
        var root = under == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(under, NativeMethods.GA_ROOT);
        return root != IntPtr.Zero && NativeMethods.WindowProcessId(root) == process.Id;
    }

    private void MoveGuarded(Point p)
    {
        Guard(p);
        // AI-NOTE: set the position directly rather than FlaUI's animated MoveTo, whose path from the
        // user's cursor could pass over other programs' windows.
        Mouse.Position = p;
        Thread.Sleep(15);
    }

    public void Click(Point p)
    {
        MoveGuarded(p);
        Mouse.Click(MouseButton.Left);
        Wait.UntilInputIsProcessed();
    }

    public void Click(AutomationElement element) => Click(Center(element));

    /// <summary>Press at <paramref name="from"/>, 10 px in 2 px steps (past WPF's drag threshold), then
    /// 10-20 steps with 15 ms sleeps to <paramref name="to"/>, release. Every step is guarded.</summary>
    public void Drag(Point from, Point to)
    {
        Guard(from);
        Guard(to);
        MoveGuarded(from);
        Mouse.Down(MouseButton.Left);
        try
        {
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            double ux = dx / length, uy = dy / length;
            for (int i = 1; i <= 5; i++)
                MoveGuarded(new Point(from.X + (int)Math.Round(ux * 2 * i), from.Y + (int)Math.Round(uy * 2 * i)));

            var start = new Point(from.X + (int)Math.Round(ux * 10), from.Y + (int)Math.Round(uy * 10));
            int steps = Math.Clamp((int)(length / 20), 10, 20);
            for (int i = 1; i <= steps; i++)
                MoveGuarded(new Point(start.X + (to.X - start.X) * i / steps, start.Y + (to.Y - start.Y) * i / steps));
        }
        finally
        {
            Mouse.Up(MouseButton.Left);
            Wait.UntilInputIsProcessed();
        }
    }

    /// <summary>Moves the pointer just outside <paramref name="element"/>'s bounds, to a point that is
    /// still inside a session window (the ✕ mouse-leave case).</summary>
    public void MoveOut(AutomationElement element)
    {
        var r = element.BoundingRectangle;
        const int margin = 12;
        var candidates = new[]
        {
            new Point(r.Left + r.Width / 2, r.Top - margin),
            new Point(r.Left + r.Width / 2, r.Bottom + margin),
            new Point(r.Left - margin, r.Top + r.Height / 2),
            new Point(r.Right + margin, r.Top + r.Height / 2),
        };
        foreach (var p in candidates)
            if (IsInsideSessionWindow(p))
            {
                MoveGuarded(p);
                Wait.UntilInputIsProcessed();
                return;
            }
        Guard(candidates[0]); // throws with the refusal message
    }

    /// <summary>Mouse wheel at <paramref name="p"/>; <paramref name="lines"/> &gt; 0 scrolls up.</summary>
    public void Wheel(Point p, double lines)
    {
        MoveGuarded(p);
        Mouse.Scroll(lines);
        Wait.UntilInputIsProcessed();
    }

    /// <summary>Presses <paramref name="chord"/> (e.g. <c>CTRL+ALT+SHIFT+F9</c>, <c>ESC</c>,
    /// <c>BACKSPACE</c>) as one chord, only once a session window is in the foreground.</summary>
    public void Keys(string chord)
    {
        var keys = ParseChord(chord);
        EnsureForeground();
        Keyboard.TypeSimultaneously(keys);
        Wait.UntilInputIsProcessed();
    }

    private void EnsureForeground()
    {
        if (IsSessionForeground()) return;
        try
        {
            _session.Home.SetForeground();
            _session.Home.Focus();
        }
        catch
        {
            // Checked below.
        }
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!IsSessionForeground() && DateTime.UtcNow < deadline) Thread.Sleep(50);
        if (!IsSessionForeground())
            throw new InvalidOperationException("Refused to send keys: no Fova window is in the foreground");
    }

    public bool IsSessionForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        return foreground != IntPtr.Zero && _session.Process is { HasExited: false } p
            && NativeMethods.WindowProcessId(foreground) == p.Id;
    }

    public static Point Center(AutomationElement element)
    {
        var r = element.BoundingRectangle;
        return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    /// <summary>Chord text (Fova's own <c>CTRL+ALT+Q</c> form) to virtual keys.</summary>
    public static VirtualKeyShort[] ParseChord(string chord)
    {
        var keys = new List<VirtualKeyShort>();
        foreach (var raw in chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = raw.ToUpperInvariant();
            ushort vk = token switch
            {
                "CTRL" or "CONTROL" => 0x11,
                "ALT" => 0x12,
                "SHIFT" => 0x10,
                "WIN" => 0x5B,
                "ESC" or "ESCAPE" => 0x1B,
                "ENTER" => 0x0D,
                "TAB" => 0x09,
                "SPACE" => 0x20,
                "BACKSPACE" => 0x08,
                "DELETE" or "DEL" => 0x2E,
                "`" => 0xC0,
                _ when token.Length == 1 && token[0] is >= 'A' and <= 'Z' or >= '0' and <= '9' => token[0],
                _ when token.Length >= 2 && token[0] == 'F' && int.TryParse(token[1..], out int f) && f is >= 1 and <= 24
                    => (ushort)(0x70 + f - 1),
                _ => throw new ArgumentException($"Unknown key '{raw}' in chord '{chord}'", nameof(chord)),
            };
            keys.Add((VirtualKeyShort)vk);
        }
        if (keys.Count == 0) throw new ArgumentException("Empty chord", nameof(chord));
        return keys.ToArray();
    }
}
