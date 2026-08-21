using System.Net;
using System.Net.Security;

namespace Overlay.Core;

/// <summary>
/// Shared TLS validation for the two local Riot endpoints — the Live Client Data API
/// (<c>https://127.0.0.1:2999</c>) and the LCU (<c>https://127.0.0.1:{port}</c>) — both of which
/// present Riot's self-signed certificate.
///
/// <para>(loop 514) The previous inline callbacks returned <c>true</c> for EVERY host; the
/// "localhost only" claim lived in a comment, enforced by nothing. Harmless today because both
/// owned clients only ever hit 127.0.0.1, but <c>PollerConfig.AllGameDataUrl</c> is a public init
/// property — any future wiring of it to user config would silently ship no-TLS-validation to
/// arbitrary remote hosts. This callback accepts an invalid chain only when the connection target
/// is a loopback address, so a remote host gets full validation again.</para>
/// </summary>
internal static class LoopbackServerCertificate
{
    /// <summary>Drop-in for <c>SslOptions.RemoteCertificateValidationCallback</c>.</summary>
    public static readonly RemoteCertificateValidationCallback Validate =
        (sender, _, _, errors) => Accept((sender as SslStream)?.TargetHostName, errors);

    /// <summary>Split out from the callback so the policy is unit-testable without an SslStream.</summary>
    internal static bool Accept(string? targetHost, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (targetHost is null) return false;
        if (targetHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(targetHost, out var ip) && IPAddress.IsLoopback(ip);
    }
}
