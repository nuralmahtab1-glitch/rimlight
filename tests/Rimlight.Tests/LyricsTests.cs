using Rimlight.Core.Lyrics;

namespace Rimlight.Tests;

public class LyricsTests
{
    private const string Sample = """
        [ar:Someone]
        [00:12.50] Fillin' me up, fill me with love
        [00:16.00] With love, yeah
        [00:19.25]
        [00:31.10] Back again
        [01:02.5][01:30.00] Chorus line
        """;

    [Fact]
    public void Parses_times_text_and_repeated_tags()
    {
        var lines = LrcParser.Parse(Sample);
        Assert.Equal(6, lines.Count);
        Assert.Equal(12.5, lines[0].Time, 3);
        Assert.Equal("Fillin' me up, fill me with love", lines[0].Text);
        Assert.Equal("", lines[2].Text);                 // instrumental marker
        Assert.Equal(62.5, lines[4].Time, 3);
        Assert.Equal(90.0, lines[5].Time, 3);
        Assert.Equal("Chorus line", lines[5].Text);
    }

    [Fact]
    public void Offset_tag_shifts_lines_earlier()
    {
        var lines = LrcParser.Parse("[offset:+500]\n[00:10.00] hi");
        Assert.Equal(9.5, lines[0].Time, 3);
    }

    [Fact]
    public void Garbage_and_empty_input_are_safe()
    {
        Assert.Empty(LrcParser.Parse(null));
        Assert.Empty(LrcParser.Parse("no tags here\njust text"));
    }

    [Fact]
    public void Timeline_reveals_words_progressively()
    {
        var t = new LyricsTimeline(LrcParser.Parse(Sample));
        Assert.Equal(-1, t.At(5).Index);                 // before the first line
        var start = t.At(12.6);
        Assert.Equal(0, start.Index);
        Assert.Equal(1, start.WordsVisible);
        var later = t.At(15.0);
        Assert.Equal(7, later.WordsVisible);             // all 7 words shown well before the next line
        Assert.False(later.IsGap);
        Assert.Equal(1, t.At(16.2).Index);
    }

    [Fact]
    public void Empty_lines_and_long_silences_are_gaps()
    {
        var t = new LyricsTimeline(LrcParser.Parse(Sample));
        Assert.True(t.At(20).IsGap);                     // empty line at 19.25
        Assert.False(t.At(32).IsGap);
        Assert.True(t.At(31.1 + LyricsTimeline.GapAfter + 1).IsGap); // long wait before the next line
    }

    [Fact]
    public void Next_line_skips_empty_lines()
    {
        var t = new LyricsTimeline(LrcParser.Parse(Sample));
        Assert.Equal("Back again", t.NextLine(1)!.Text);
    }
}
