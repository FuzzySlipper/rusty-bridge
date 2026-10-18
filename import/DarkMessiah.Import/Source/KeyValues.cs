using System.Text;

namespace DarkMessiah.Import.Source;

/// <summary>
/// A KeyValues block as material files write it: a name, string values and
/// child blocks. Keys compare without case, as the engine reads them.
/// </summary>
public sealed class KeyValues
{
    public string Name { get; }
    public List<(string Key, string Value)> Values { get; } = [];
    public List<KeyValues> Blocks { get; } = [];

    public KeyValues(string name) => Name = name;

    public string? this[string key] => Values.LastOrDefault(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    public KeyValues? Block(string name) => Blocks.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first top-level block of a file, or null when there is none.</summary>
    public static KeyValues? Parse(byte[] bytes)
    {
        var tokens = Tokens(Encoding.Latin1.GetString(bytes)).GetEnumerator();
        if (!tokens.MoveNext()) return null;
        string name = tokens.Current.Text;
        if (!tokens.MoveNext() || tokens.Current.Text != "{" || tokens.Current.Quoted) return null;
        return ReadBlock(name, tokens);
    }

    static KeyValues ReadBlock(string name, IEnumerator<(string Text, bool Quoted)> tokens)
    {
        var block = new KeyValues(name);
        while (tokens.MoveNext())
        {
            var (key, quoted) = tokens.Current;
            if (key == "}" && !quoted) break;
            if (!tokens.MoveNext()) break;
            var (value, valueQuoted) = tokens.Current;
            if (value == "{" && !valueQuoted) block.Blocks.Add(ReadBlock(key, tokens));
            else block.Values.Add((key, value));
        }
        return block;
    }

    static IEnumerable<(string Text, bool Quoted)> Tokens(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c is '{' or '}') { i++; yield return (c.ToString(), false); continue; }
            if (c == '"')
            {
                int end = s.IndexOf('"', i + 1);
                if (end < 0) end = s.Length;
                yield return (s[(i + 1)..end], true);
                i = end + 1;
                continue;
            }
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '"')) i++;
            yield return (s[start..i], false);
        }
    }
}
