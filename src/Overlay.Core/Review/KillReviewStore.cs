using System.Globalization;
using System.Text.Json;

namespace Overlay.Core.Review;

/// <summary>
/// (M42) Finished <see cref="GameReview"/>s on disk, one JSON file per game
/// (<c>{yyyyMMdd-HHmmss}.json</c> by game start), the newest <see cref="Keep"/> kept. Local only (P1).
/// Never throws: an unreadable file is skipped, a failed write loses that one game.
/// </summary>
public sealed class KillReviewStore
{
    public const int Keep = 50;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _dir;

    public KillReviewStore(string directory) => _dir = directory;

    /// <summary>Raised after a review was saved (on the saving thread).</summary>
    public event Action<GameReview>? Saved;

    public void Save(GameReview review)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, review.StartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(review, Options));
            foreach (var old in Files().Skip(Keep)) File.Delete(old);
            Saved?.Invoke(review);
        }
        catch { /* a review is a convenience; losing one must never affect the app */ }
    }

    /// <summary>All kept reviews, newest first.</summary>
    public IReadOnlyList<GameReview> LoadAll()
    {
        var list = new List<GameReview>();
        foreach (var file in Files())
        {
            try
            {
                if (JsonSerializer.Deserialize<GameReview>(File.ReadAllText(file), Options) is { } r) list.Add(r);
            }
            catch { /* skip a damaged file */ }
        }
        return list;
    }

    private IEnumerable<string> Files()
        => Directory.Exists(_dir)
            ? Directory.GetFiles(_dir, "*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            : Enumerable.Empty<string>();
}
