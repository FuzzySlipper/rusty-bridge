using System.Text;

namespace RustyBridge.Wire.Donor;

/// <summary>
/// Sequence names of a Source studio model (.mdl version 44, as Dark Messiah
/// ships them), in the order the engine numbers them: the model's own
/// sequences, then each $includemodel's, depth first, skipping names already
/// present (virtualmodel_t::AppendModels/AppendSequences). That order is what
/// CBaseAnimating::m_nSequence indexes.
/// </summary>
public static class StudioModel
{
    const int SeqDescBytes = 212;

    public static IReadOnlyList<string> SequenceNames(string path, Func<string, byte[]?> read)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>();
        Append(path, read, names, seen, visited);
        return names;
    }

    static void Append(string path, Func<string, byte[]?> read, List<string> names, HashSet<string> seen, HashSet<string> visited)
    {
        if (!visited.Add(VpkIndex.Normalize(path))) return;
        var b = read(path);
        if (b is null || b.Length < 344 || Encoding.ASCII.GetString(b, 0, 4) != "IDST") return;
        int count = BitConverter.ToInt32(b, 188), index = BitConverter.ToInt32(b, 192);
        for (int i = 0; i < count; i++)
        {
            int desc = index + i * SeqDescBytes;
            string name = CString(b, desc + BitConverter.ToInt32(b, desc + 4));
            if (seen.Add(name)) names.Add(name);
        }
        int includes = BitConverter.ToInt32(b, 336), includeIndex = BitConverter.ToInt32(b, 340);
        for (int k = 0; k < includes; k++)
        {
            int group = includeIndex + k * 8;
            Append(CString(b, group + BitConverter.ToInt32(b, group + 4)), read, names, seen, visited);
        }
    }

    /// <summary>
    /// The animation events of every sequence of a model and its includes, by
    /// sequence name (first definition wins, as with <see cref="SequenceNames"/>):
    /// (cycle, event id) pairs in cycle order. The mstudioevent_t records are
    /// 80 bytes, cycle at +0 and event at +4, from the seqdesc's count (+24)
    /// and offset (+28).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<(float Cycle, int Event)>> Events(string path, Func<string, byte[]?> read)
    {
        var events = new Dictionary<string, IReadOnlyList<(float, int)>>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>();
        void Append(string model)
        {
            if (!visited.Add(VpkIndex.Normalize(model))) return;
            var b = read(model);
            if (b is null || b.Length < 344 || Encoding.ASCII.GetString(b, 0, 4) != "IDST") return;
            int count = BitConverter.ToInt32(b, 188), index = BitConverter.ToInt32(b, 192);
            for (int i = 0; i < count; i++)
            {
                int desc = index + i * SeqDescBytes;
                string name = CString(b, desc + BitConverter.ToInt32(b, desc + 4));
                int n = BitConverter.ToInt32(b, desc + 24), at = desc + BitConverter.ToInt32(b, desc + 28);
                var list = new List<(float, int)>();
                for (int k = 0; k < n; k++) list.Add((BitConverter.ToSingle(b, at + k * 80), BitConverter.ToInt32(b, at + k * 80 + 4)));
                events.TryAdd(name, list.OrderBy(e => e.Item1).ToList());
            }
            int includes = BitConverter.ToInt32(b, 336), includeIndex = BitConverter.ToInt32(b, 340);
            for (int k = 0; k < includes; k++)
            {
                int group = includeIndex + k * 8;
                Append(CString(b, group + BitConverter.ToInt32(b, group + 4)));
            }
        }
        Append(path);
        return events;
    }

    static string CString(byte[] b, int at)
    {
        int end = Array.IndexOf(b, (byte)0, at);
        return Encoding.ASCII.GetString(b, at, end - at);
    }
}
