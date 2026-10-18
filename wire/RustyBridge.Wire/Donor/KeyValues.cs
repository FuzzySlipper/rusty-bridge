using System.Text;

namespace RustyBridge.Wire.Donor;

/// <summary>
/// A KeyValues block as the donor's text files write it (materials, model
/// $keyvalues sources): a name, string values and child blocks. Keys may be
/// quoted or bare and compare without case, as the engine reads them.
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
    public static KeyValues? Parse(byte[] bytes) => ParseAll(bytes).FirstOrDefault();

    /// <summary>
    /// Every top-level block of a file. A block left open at the end of the
    /// file (Arkane's npc_orc.qct misses a brace) ends there.
    /// </summary>
    public static List<KeyValues> ParseAll(byte[] bytes)
    {
        var blocks = new List<KeyValues>();
        var tokens = Tokens(Encoding.Latin1.GetString(bytes)).GetEnumerator();
        while (tokens.MoveNext())
        {
            string name = tokens.Current.Text;
            if (!tokens.MoveNext()) break;
            if (tokens.Current.Text == "{" && !tokens.Current.Quoted) blocks.Add(ReadBlock(name, tokens));
        }
        return blocks;
    }

    /// <summary>The first block named <paramref name="name"/> here or below, depth first.</summary>
    public KeyValues? Find(string name)
    {
        foreach (var b in Blocks)
        {
            if (b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return b;
            if (b.Find(name) is KeyValues deeper) return deeper;
        }
        return null;
    }

    /// <summary>A value as a number, or null when absent or not a number.</summary>
    public float? Number(string key) =>
        this[key] is string v && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : null;

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
