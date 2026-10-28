using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MacShell.Core;

// Song lyrics for the island, from LRCLIB (https://lrclib.net): a free, open lyrics database, no account or key.
// Windows' media info has no lyrics, and Spotify doesn't share its own with other apps.
// Only on demand (the island's lyrics card): sends the track's title, artist, album and length, nothing else.
// Results (also "none found") are cached in memory per track, so each song is looked up once per run.
public sealed partial class LyricsService
{
    // Time = when the line starts, from the start of the track. Untimed (plain) lyrics have every Time = -1.
    public readonly record struct Line(TimeSpan Time, string Text);

    public sealed record Lyrics(IReadOnlyList<Line> Lines, bool Synced, bool Instrumental)
    {
        public static readonly Lyrics None = new([], false, false);
    }

    private const int CacheSize = 50;
    // Created on the first lookup, not at startup: networking isn't loaded until lyrics are asked for.
    private static readonly Lazy<HttpClient> Http = new(CreateClient);
    private readonly Dictionary<string, Lyrics> _cache = [];
    private readonly Queue<string> _order = new();

    // Never throws: no network, timeouts and unknown songs all give Lyrics.None.
    public async Task<Lyrics> FindAsync(string title, string artist, string album, TimeSpan duration)
    {
        string key = $"{artist}|{title}|{album}".ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var (cleanTitle, cleanArtist) = Clean(title, artist);

        // 1. Exact: title + artist (+ album, + duration in whole seconds), the way LRCLIB recommends.
        var query = $"track_name={Uri.EscapeDataString(cleanTitle)}&artist_name={Uri.EscapeDataString(cleanArtist)}";
        if (album.Length > 0) query += $"&album_name={Uri.EscapeDataString(album)}";
        if (duration > TimeSpan.Zero) query += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
        var (exact, exactAnswered) = await TryGetJson($"https://lrclib.net/api/get?{query}");
        Lyrics result;
        using (exact) result = exact is null ? Lyrics.None : ParseElement(exact.RootElement);
        bool answered = exactAnswered;

        // 2. Fuzzy: a search, first hit with synced lyrics (else the first with any). Also when step 1 failed:
        // LRCLIB's exact endpoint sometimes answers 503 or times out while search works (seen 2026-10-28).
        if (result.Lines.Count == 0 && !result.Instrumental)
        {
            var (hits, searchAnswered) = await TryGetJson($"https://lrclib.net/api/search?q={Uri.EscapeDataString(cleanArtist + " " + cleanTitle)}");
            answered = searchAnswered;
            using (hits)
            {
                if (hits?.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var all = hits.RootElement.EnumerateArray().Take(10).ToList();
                    var best = all.FirstOrDefault(h => Has(h, "syncedLyrics"));
                    if (best.ValueKind == JsonValueKind.Undefined) best = all.FirstOrDefault(h => Has(h, "plainLyrics"));
                    if (best.ValueKind != JsonValueKind.Undefined) result = ParseElement(best);
                }
            }
        }

        // Cache lyrics, and "not in the database" — but not a network failure, so the next click tries again.
        if (result.Lines.Count > 0 || result.Instrumental || answered)
        {
            _cache[key] = result;
            _order.Enqueue(key);
            if (_order.Count > CacheSize) _cache.Remove(_order.Dequeue());
        }
        return result;
    }

    // The line playing at `position` (-1 before the first one). For synced lyrics only.
    public static int LineAt(Lyrics lyrics, TimeSpan position)
    {
        int index = -1;
        for (int i = 0; i < lyrics.Lines.Count && lyrics.Lines[i].Time <= position; i++) index = i;
        return index;
    }

    // (document, answered): answered = LRCLIB really replied (found, or 404 "not in the database"); false for
    // network errors, timeouts (6 s per request) and server errors. Never throws.
    private static async Task<(JsonDocument? Doc, bool Answered)> TryGetJson(string url)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var response = await Http.Value.GetAsync(url, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, true);
            if (!response.IsSuccessStatusCode) return (null, false);
            return (JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token)), true);
        }
        catch (Exception) { return (null, false); }   // offline, blocked, timed out, bad JSON
    }

    private static Lyrics ParseElement(JsonElement e)
    {
        bool instrumental = e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
        if (Has(e, "syncedLyrics")) return new(ParseLrc(e.GetProperty("syncedLyrics").GetString()!), true, instrumental);
        if (Has(e, "plainLyrics"))
        {
            var lines = e.GetProperty("plainLyrics").GetString()!.Split('\n').Select(l => new Line(TimeSpan.FromTicks(-1), l.Trim())).ToList();
            return new(lines, false, instrumental);
        }
        return new([], false, instrumental);
    }

    private static bool Has(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0;

    // LRC: "[01:23.45] text" (a line can carry several timestamps: the same text at several times).
    private static List<Line> ParseLrc(string lrc)
    {
        var lines = new List<Line>();
        foreach (string raw in lrc.Split('\n'))
        {
            var stamps = TimeStamp().Matches(raw);
            if (stamps.Count == 0) continue;
            string text = TimeStamp().Replace(raw, "").Trim();
            foreach (Match m in stamps)
            {
                double seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                    + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                lines.Add(new Line(TimeSpan.FromSeconds(seconds), text));
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    // Video titles are messy: "Muse - Uprising [Official Video]" by channel "Muse" -> ("Uprising", "Muse").
    private static (string Title, string Artist) Clean(string title, string artist)
    {
        string t = Brackets().Replace(title, "").Trim();
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            string left = t[..dash].Trim(), right = t[(dash + 3)..].Trim();
            if (artist.Length == 0 || left.Contains(artist, StringComparison.OrdinalIgnoreCase) || artist.Contains(left, StringComparison.OrdinalIgnoreCase))
                return (right, artist.Length > 0 ? artist : left);
        }
        string a = artist.EndsWith(" - Topic", StringComparison.Ordinal) ? artist[..^8] : artist;   // YouTube Music channels
        return (t, a);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };   // per-request limits are in TryGetJson
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MacShell/1.0");   // LRCLIB asks clients to identify themselves
        return client;
    }

    [GeneratedRegex(@"\[(\d+):(\d+(?:\.\d+)?)\]")]
    private static partial Regex TimeStamp();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*(official|video|audio|lyric|lyrics|visualizer|hd|4k|remaster)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Brackets();
}
