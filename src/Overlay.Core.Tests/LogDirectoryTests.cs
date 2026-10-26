using Overlay.Core.Logging;

namespace Overlay.Core.Tests;

/// <summary>
/// <see cref="LogDirectory"/>: Core's file logs default to <c>logs/</c> next to the exe, and
/// <see cref="Logger.DefaultLogDirectory"/> follows it when Program.Main moves it (test mode).
/// </summary>
public class LogDirectoryTests
{
    private static readonly string Default = Path.Combine(AppContext.BaseDirectory, "logs");

    [Fact]
    public void Default_IsLogsUnderBaseDirectory()
        => Assert.Equal(Default, LogDirectory.Current);

    [Fact]
    public void Set_LoggerDefaultLogDirectoryFollows()
    {
        string moved = Path.Combine(Path.GetTempPath(), "fova-logdir-test");
        try
        {
            LogDirectory.Current = moved;
            Assert.Equal(moved, Logger.DefaultLogDirectory);
        }
        finally
        {
            LogDirectory.Current = Default;
        }
    }
}
