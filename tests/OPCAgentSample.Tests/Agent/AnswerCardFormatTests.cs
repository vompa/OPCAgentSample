using System.Text.Json;
using OPCAgentSample.Server.Agent;

namespace OPCAgentSample.Tests.Agent;

public sealed class AnswerCardFormatTests
{
    private const string Chart =
        """{"title":"Ist/Soll","max":110,"unit":" %","decimals":0,"bars":[{"label":"RT-01","value":102,"rating":"ok"},{"label":"RT-03","value":71,"rating":"crit"},{"label":"VP-02","value":88,"rating":"warn"}]}""";

    private static string Card(string chart = "null", string rows = """[{"topic":"RT-03","data":"Störung","rating":"crit"}]""", string headline = "Kernaussage.") =>
        $$"""{"headline":"{{headline}}","todo":[],"table":{"rows":{{rows}}},"chart":{{chart}},"why":["Grund."]}""";

    [Fact]
    public void Valid_card_is_read_with_table_chart_and_form()
    {
        Assert.True(AnswerCardFormat.TryRead(Card(Chart), out var card, out _));

        Assert.Equal("Kernaussage.", card.Headline);
        Assert.Equal("crit", card.Table.Rows.Single().Rating);
        Assert.Equal(3, card.Chart!.Bars.Count);
        Assert.Equal("Tabelle + Balken", card.Form);
    }

    [Fact]
    public void Card_without_values_is_text_only()
    {
        Assert.True(AnswerCardFormat.TryRead(Card(rows: "[]"), out var card, out _));

        Assert.Equal("Text", card.Form);
    }

    [Theory]
    [InlineData("kein json")]
    [InlineData("null")]
    [InlineData("""{"headline":"","todo":[],"table":{"rows":[]},"chart":null,"why":[]}""")]
    public void Unusable_text_is_rejected(string json)
    {
        Assert.False(AnswerCardFormat.TryRead(json, out _, out var problem));
        Assert.NotEmpty(problem);
    }

    [Fact]
    public void Row_with_wrong_cell_count_or_unknown_rating_is_rejected()
    {
        Assert.False(AnswerCardFormat.TryRead(Card(rows: """[{"topic":"","data":"nur eine","rating":"ok"}]"""), out _, out _));
        Assert.False(AnswerCardFormat.TryRead(Card(rows: """[{"topic":"a","data":"b","rating":"rot"}]"""), out _, out _));
    }

    [Fact]
    public void Chart_with_value_above_scale_or_no_bars_is_rejected()
    {
        Assert.False(AnswerCardFormat.TryRead(Card(Chart.Replace("\"max\":110", "\"max\":90", StringComparison.Ordinal)), out _, out _));
        Assert.False(AnswerCardFormat.TryRead(Card("""{"title":"x","max":10,"unit":"","decimals":0,"bars":[]}"""), out _, out _));
    }

    [Fact]
    public void Too_many_rows_or_long_headline_are_rejected()
    {
        var rows = "[" + string.Join(',', Enumerable.Repeat("""{"topic":"a","data":"b","rating":"ok"}""", AnswerCardFormat.MaxRows + 1)) + "]";
        Assert.False(AnswerCardFormat.TryRead(Card(rows: rows), out _, out _));
        Assert.False(AnswerCardFormat.TryRead(Card(headline: new string('x', AnswerCardFormat.MaxHeadlineLength + 1)), out _, out _));
    }

    [Fact]
    public void Schema_closes_every_object_and_lists_only_supported_keywords()
    {
        var text = JsonSerializer.Serialize(AnswerCardFormat.Schema());

        foreach (var unsupported in new[] { "minimum", "maximum", "minLength", "maxLength", "multipleOf", "$ref" })
            Assert.DoesNotContain($"\"{unsupported}\"", text, StringComparison.Ordinal);
        var objects = System.Text.RegularExpressions.Regex.Count(text, "\"type\":\"object\"");
        var closed = System.Text.RegularExpressions.Regex.Count(text, "\"additionalProperties\":false");
        Assert.Equal(objects, closed);
    }

    [Fact]
    public void Options_are_validated_only_with_a_key_and_reject_unknown_effort_and_non_loopback_url()
    {
        Assert.Null(new ClaudeOptions { Effort = "wild" }.Validate());
        Assert.Null(new ClaudeOptions { ApiKey = "k" }.Validate());
        Assert.Contains("Effort", new ClaudeOptions { ApiKey = "k", Effort = "wild" }.Validate(), StringComparison.Ordinal);
        Assert.Contains("McpUrl", new ClaudeOptions { ApiKey = "k", McpUrl = "http://example.org/mcp" }.Validate(), StringComparison.Ordinal);
        Assert.Contains("MaxRounds", new ClaudeOptions { ApiKey = "k", MaxRounds = 0 }.Validate(), StringComparison.Ordinal);
    }
}
