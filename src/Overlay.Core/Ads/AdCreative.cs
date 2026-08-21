using System.Text.Json.Serialization;

namespace Overlay.Core.Ads;

/// <summary>
/// M29 §D1: one static-image creative served by our own endpoint. No JS, no HTML, no video —
/// just the image to draw, where a click goes, and an optional impression beacon.
/// </summary>
public sealed class AdCreative
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>HTTPS URL of a static JPEG/PNG/WebP (max 300 KB, max 800x200 — M29 §2). Enforced
    /// at parse since loop 514: non-https images drop the creative (plain http passes for loopback
    /// hosts only, so the local test server works), and the bytes are magic-byte checked.</summary>
    [JsonPropertyName("image")]
    public string Image { get; set; } = "";

    /// <summary>Opened in the SYSTEM browser on click (M29 §3) — never navigated in-process.</summary>
    [JsonPropertyName("click")]
    public string Click { get; set; } = "";

    /// <summary>Optional beacon; pinged batched on shutdown, never per-second (M29 §2).</summary>
    [JsonPropertyName("impression")]
    public string? Impression { get; set; }
}

/// <summary>The endpoint's response shape: a flat list of creatives to rotate through.</summary>
public sealed class AdManifest
{
    [JsonPropertyName("creatives")]
    public List<AdCreative> Creatives { get; set; } = new();
}

/// <summary>A creative plus its (size-checked) image bytes, ready for the client to decode.</summary>
public sealed record AdImage(AdCreative Creative, byte[] Bytes);
