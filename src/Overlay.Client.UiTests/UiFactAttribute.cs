namespace Overlay.Client.UiTests;

/// <summary>Whether UI tests may run: only when <c>FOVA_UI_TESTS=1</c>, because they take the real
/// mouse and keyboard.</summary>
public static class UiGate
{
    public const string EnvVar = "FOVA_UI_TESTS";

    public const string SkipReason = "UI test: set FOVA_UI_TESTS=1 to run (uses the real mouse and keyboard)";

    public static bool Enabled => Environment.GetEnvironmentVariable(EnvVar) == "1";
}

/// <summary>A UI test: skipped unless <see cref="UiGate.Enabled"/>.</summary>
public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute()
    {
        if (!UiGate.Enabled) Skip = UiGate.SkipReason;
    }
}

/// <summary>A UI theory: skipped unless <see cref="UiGate.Enabled"/>.</summary>
public sealed class UiTheoryAttribute : TheoryAttribute
{
    public UiTheoryAttribute()
    {
        if (!UiGate.Enabled) Skip = UiGate.SkipReason;
    }
}
