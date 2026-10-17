using System.Text;

namespace OPCAgentSample.Server.Agent;

/// <summary>Der bereits vollständig eingetroffene Anfang der Antwortkarte: Überschrift und fertige Was-tun-Einträge.</summary>
public sealed record AnswerDraft(string Headline, IReadOnlyList<string> Todo);

/// <summary>
/// Liest aus dem wachsenden JSON-Text der Antwortkarte die Überschrift (<c>headline</c>) und die fertigen Einträge von <c>todo</c>,
/// sobald sie vollständig sind. Das Schema legt die Reihenfolge fest (Überschrift, Was tun, danach Tabelle usw.); der Extraktor
/// liest nur diesen Anfang und hört bei jedem anderen Schlüssel auf. Text, der kein JSON-Objekt ist (z. B. ein Satz vor einem
/// Tool-Aufruf), ergibt nie einen Entwurf. Der Entwurf dient nur der Anzeige: Gültig ist allein die geprüfte Karte.
/// </summary>
public sealed class AnswerDraftExtractor
{
    private readonly StringBuilder _text = new();
    private AnswerDraft? _last;

    /// <summary>Hängt ein Stück Text an. <see langword="true"/>, wenn sich der Entwurf gegenüber dem letzten gemeldeten geändert hat.</summary>
    public bool TryAppend(string delta, out AnswerDraft draft)
    {
        ArgumentNullException.ThrowIfNull(delta);
        _text.Append(delta);
        draft = null!;

        var current = Read(_text.ToString());
        if (current is null || (_last is not null && Same(_last, current))) return false;

        _last = current;
        draft = current;
        return true;
    }

    /// <summary>Liest den Entwurf aus dem bisherigen Text; <see langword="null"/>, solange die Überschrift nicht vollständig ist.</summary>
    internal static AnswerDraft? Read(string text)
    {
        var position = 0;
        SkipWhitespace(text, ref position);
        if (!Expect(text, ref position, '{')) return null;

        string? headline = null;
        var todo = new List<string>();
        while (true)
        {
            SkipWhitespace(text, ref position);
            if (!ReadString(text, ref position, out var key)) break;
            SkipWhitespace(text, ref position);
            if (!Expect(text, ref position, ':')) break;
            SkipWhitespace(text, ref position);

            if (key == "headline" && headline is null)
            {
                if (!ReadString(text, ref position, out var value)) break;
                headline = value;
            }
            else if (key == "todo" && headline is not null)
            {
                if (!ReadStringArray(text, ref position, todo)) break;
            }
            else
            {
                break;
            }

            SkipWhitespace(text, ref position);
            if (!Expect(text, ref position, ',')) break;
        }

        return headline is null ? null : new AnswerDraft(Clamp(headline, AnswerCardFormat.MaxHeadlineLength), ClampTodo(todo));
    }

    private static bool Same(AnswerDraft a, AnswerDraft b) => a.Headline == b.Headline && a.Todo.SequenceEqual(b.Todo, StringComparer.Ordinal);

    private static string Clamp(string text, int max) => text.Length > max ? text[..max] : text;

    private static List<string> ClampTodo(List<string> todo) =>
        [.. todo.Take(AnswerCardFormat.MaxTodoItems).Select(t => Clamp(t, AnswerCardFormat.MaxTextLength))];

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
    }

    private static bool Expect(string text, ref int position, char expected)
    {
        if (position >= text.Length || text[position] != expected) return false;

        position++;
        return true;
    }

    /// <summary>
    /// Liest die Einträge eines String-Arrays ab <paramref name="position"/>. <see langword="true"/> nur, wenn das Array vollständig
    /// geschlossen ist; bereits fertige Einträge landen auch bei <see langword="false"/> in <paramref name="items"/>.
    /// </summary>
    private static bool ReadStringArray(string text, ref int position, List<string> items)
    {
        if (!Expect(text, ref position, '[')) return false;

        items.Clear();
        while (true)
        {
            SkipWhitespace(text, ref position);
            if (Expect(text, ref position, ']')) return true;
            if (!ReadString(text, ref position, out var item)) return false;

            items.Add(item);
            SkipWhitespace(text, ref position);
            if (Expect(text, ref position, ',')) continue;
            if (Expect(text, ref position, ']')) return true;
            return false;
        }
    }

    /// <summary>Liest einen JSON-String samt Escapes (<c>\" \\ \/ \b \f \n \r \t \uXXXX</c>); <see langword="false"/>, solange er nicht geschlossen ist.</summary>
    private static bool ReadString(string text, ref int position, out string value)
    {
        value = string.Empty;
        var index = position;
        if (index >= text.Length || text[index] != '"') return false;

        index++;
        var result = new StringBuilder();
        while (index < text.Length)
        {
            var c = text[index];
            if (c == '"')
            {
                position = index + 1;
                value = result.ToString();
                return true;
            }

            if (c != '\\')
            {
                result.Append(c);
                index++;
                continue;
            }

            if (index + 1 >= text.Length) return false;

            var escape = text[index + 1];
            if (escape == 'u')
            {
                if (index + 6 > text.Length || !ushort.TryParse(text.AsSpan(index + 2, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var code))
                    return false;

                result.Append((char)code);
                index += 6;
                continue;
            }

            result.Append(escape switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'b' => '\b',
                'f' => '\f',
                _ => escape, // " \ /
            });
            index += 2;
        }

        return false;
    }
}
