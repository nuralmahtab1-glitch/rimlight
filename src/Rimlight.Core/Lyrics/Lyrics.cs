using System.Globalization;
using System.Text.RegularExpressions;

namespace Rimlight.Core.Lyrics;

/// <summary>One timed lyric line. Empty <see cref="Text"/> marks an instrumental gap.</summary>
public sealed record LyricLine(double Time, string Text)
{
    public string[] Words { get; } = Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Parses LRC ("[mm:ss.xx] text") synced lyrics, as served by LRCLIB.</summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    public static List<LyricLine> Parse(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return lines;
        double offset = 0;
        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var off = OffsetTag().Match(line);
            if (off.Success) { offset = int.Parse(off.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0; continue; }

            var tags = TimeTag().Matches(line);
            if (tags.Count == 0) continue;
            // Text follows the last tag; a line may carry several timestamps ("[00:10][00:50] chorus").
            var text = line[(tags[^1].Index + tags[^1].Length)..].Trim();
            foreach (Match m in tags)
            {
                double min = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                double frac = 0;
                if (m.Groups[3].Success)
                {
                    var f = m.Groups[3].Value;
                    frac = double.Parse(f, CultureInfo.InvariantCulture) / Math.Pow(10, f.Length);
                }
                // Positive [offset:] means lyrics should appear earlier.
                lines.Add(new LyricLine(Math.Max(0, min * 60 + sec + frac - offset), text));
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }
}

/// <summary>What to show at a given playback position.</summary>
/// <param name="Index">Current line index, or −1 before the first line.</param>
/// <param name="WordsVisible">How many words of the current line have been "sung" so far.</param>
/// <param name="IsGap">True during an instrumental break (empty line or a long silence before the next line).</param>
public readonly record struct LyricsPosition(int Index, int WordsVisible, bool IsGap);

/// <summary>
/// Maps playback position → current line and word. LRC only times whole lines, so words are revealed
/// progressively through the first part of each line (fast enough to keep up with the singer).
/// </summary>
public sealed class LyricsTimeline
{
    /// <summary>Seconds of no new line after which we treat it as an instrumental break.</summary>
    public const double GapAfter = 8.0;

    public IReadOnlyList<LyricLine> Lines { get; }

    public LyricsTimeline(IReadOnlyList<LyricLine> lines) => Lines = lines;

    public LyricsPosition At(double position)
    {
        if (Lines.Count == 0 || position < Lines[0].Time) return new LyricsPosition(-1, 0, true);

        // Binary search: last line with Time <= position.
        int lo = 0, hi = Lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Lines[mid].Time <= position) lo = mid; else hi = mid - 1;
        }
        var line = Lines[lo];
        double end = lo + 1 < Lines.Count ? Lines[lo + 1].Time : line.Time + 6;
        double since = position - line.Time;
        int words = line.Words.Length;

        bool gap = words == 0 || since > GapAfter;
        if (gap) return new LyricsPosition(lo, words, true);

        // Reveal words across ~70% of the line's duration, but no slower than 0.45 s per word.
        double duration = Math.Max(0.3, end - line.Time);
        double perWord = Math.Min(0.45, duration * 0.7 / words);
        int visible = Math.Clamp(1 + (int)(since / perWord), 1, words);
        return new LyricsPosition(lo, visible, false);
    }

    /// <summary>The next non-empty line after <paramref name="index"/>, or null.</summary>
    public LyricLine? NextLine(int index)
    {
        for (int i = index + 1; i < Lines.Count; i++)
            if (Lines[i].Words.Length > 0) return Lines[i];
        return null;
    }
}
