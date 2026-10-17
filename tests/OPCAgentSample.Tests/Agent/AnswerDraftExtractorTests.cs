using OPCAgentSample.Server.Agent;

namespace OPCAgentSample.Tests.Agent;

/// <summary>Der Extraktor liest aus dem wachsenden JSON der Karte Überschrift und fertige Was-tun-Einträge.</summary>
public sealed class AnswerDraftExtractorTests
{
    private static AnswerDraft? Feed(string text, int chunk = 7)
    {
        var extractor = new AnswerDraftExtractor();
        AnswerDraft? last = null;
        for (var start = 0; start < text.Length; start += chunk)
        {
            if (extractor.TryAppend(text.Substring(start, Math.Min(chunk, text.Length - start)), out var draft)) last = draft;
        }

        return last;
    }

    [Fact]
    public void Complete_card_yields_headline_and_all_todo_items()
    {
        var draft = Feed(Replies.ValidCard);

        Assert.NotNull(draft);
        Assert.Equal("RT-03 steht seit 05:12 und kostet Menge.", draft.Headline);
        Assert.Equal(["Instandhaltung beim Greifer von RT-03 nachfragen."], draft.Todo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"headline""")]
    [InlineData("""{"headline":""")]
    [InlineData("""{"headline":"Noch nicht fertig""")]
    [InlineData("""{"headline":"Ende mit Escape\""")]
    [InlineData("""{"headline":"Unicode \u00""")]
    public void Incomplete_headline_yields_no_draft(string partial)
    {
        Assert.Null(AnswerDraftExtractor.Read(partial));
    }

    [Fact]
    public void Headline_is_reported_as_soon_as_its_string_is_closed_and_todo_items_follow_one_by_one()
    {
        var extractor = new AnswerDraftExtractor();

        Assert.False(extractor.TryAppend("{\"headline\":\"RT-03 steht", out _));
        Assert.True(extractor.TryAppend(" still.\",\"todo\":[\"Erster Schritt\"", out var first));
        Assert.Equal("RT-03 steht still.", first.Headline);
        Assert.Equal(["Erster Schritt"], first.Todo);
        Assert.False(extractor.TryAppend(",\"Zweiter Sch", out _));
        Assert.True(extractor.TryAppend("ritt\"]", out var second));
        Assert.Equal(["Erster Schritt", "Zweiter Schritt"], second.Todo);
        Assert.False(extractor.TryAppend(",\"table\":{\"rows\":[]}", out _));
    }

    [Fact]
    public void Headline_alone_is_reported_before_todo_starts()
    {
        var draft = AnswerDraftExtractor.Read("""{"headline":"Alles läuft.","todo":[""");

        Assert.NotNull(draft);
        Assert.Equal("Alles läuft.", draft.Headline);
        Assert.Empty(draft.Todo);
    }

    [Fact]
    public void Escapes_are_decoded()
    {
        var draft = AnswerDraftExtractor.Read("""{"headline":"Er sagte \"stopp\" \\ a\/b\nZeile über € 😀","todo":[]}""");

        Assert.NotNull(draft);
        Assert.Equal("Er sagte \"stopp\" \\ a/b\nZeile über € 😀", draft.Headline);
    }

    [Fact]
    public void Escape_split_between_chunks_is_not_misread()
    {
        var json = """{"headline":"Zuä\"x","todo":[]}""";
        for (var cut = 1; cut < json.Length; cut++)
        {
            var extractor = new AnswerDraftExtractor();
            AnswerDraft? last = null;
            foreach (var part in new[] { json[..cut], json[cut..] })
            {
                if (extractor.TryAppend(part, out var draft)) last = draft;
            }

            Assert.Equal("Zuä\"x", last?.Headline);
        }
    }

    [Theory]
    [InlineData("Ich schaue kurz nach.")]
    [InlineData("[1,2]")]
    [InlineData("""{"table":{"rows":[]},"headline":"x"}""")]
    public void Text_that_is_not_the_start_of_a_card_yields_no_draft(string text)
    {
        Assert.Null(AnswerDraftExtractor.Read(text));
    }

    [Fact]
    public void Draft_is_clamped_to_the_limits_of_the_card()
    {
        var overlong = new string('x', AnswerCardFormat.MaxHeadlineLength + 50);

        var draft = AnswerDraftExtractor.Read($$"""{"headline":"{{overlong}}","todo":["a","b","c","d","e"]}""");

        Assert.NotNull(draft);
        Assert.Equal(AnswerCardFormat.MaxHeadlineLength, draft.Headline.Length);
        Assert.Equal(AnswerCardFormat.MaxTodoItems, draft.Todo.Count);
    }

    [Fact]
    public void Unchanged_draft_is_not_reported_twice()
    {
        var extractor = new AnswerDraftExtractor();

        Assert.True(extractor.TryAppend("""{"headline":"A","todo":[]""", out _));
        Assert.False(extractor.TryAppend(",", out _));
        Assert.False(extractor.TryAppend("\"table\":", out _));
    }
}
