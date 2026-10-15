namespace Coach.Core;

/// <summary>Small, presentation-ready segments rather than raw paragraphs from an LLM.</summary>
public sealed record AdviceCard(string Move, string Target, string Next, string Reason)
{
    public static AdviceCard Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new("Brak rekomendacji", "", "", "Model nie podał konkretnego ruchu.");

        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var loose = new List<string>();
        string[] labels = ["RUCH:", "KARTA:", "CEL:", "POTEM:", "KOLEJNOŚĆ:", "DLACZEGO:", "UWAGA:"];
        foreach (var original in raw.Replace("\r", "").Split('\n'))
        {
            var line = original.Trim().Trim('*').Trim();
            if (line.Length == 0) continue;
            var label = labels.FirstOrDefault(x => line.StartsWith(x, StringComparison.OrdinalIgnoreCase));
            if (label is not null)
            {
                current = label[..^1].ToUpperInvariant();
                if (!fields.TryGetValue(current, out var items))
                    fields[current] = items = [];
                var value = line[label.Length..].Trim().Trim('*').Trim();
                if (value.Length > 0) items.Add(value);
            }
            else if (current is not null) fields[current].Add(line);
            else loose.Add(line);
        }

        string Read(params string[] keys) => string.Join(" ", keys.SelectMany(k =>
            fields.TryGetValue(k, out var v) ? (IEnumerable<string>)v : Array.Empty<string>()).Where(s => s.Length > 0));

        string move = Read("RUCH", "KARTA");
        if (move.Length == 0) move = loose.FirstOrDefault() ?? "Sprawdź dostępne zagrania";
        string next = Read("POTEM", "KOLEJNOŚĆ");
        string reason = Read("DLACZEGO");
        string caveat = Read("UWAGA");
        if (caveat.Length > 0)
            reason = reason.Length > 0 ? reason + "  ·  Uwaga: " + caveat : caveat;
        if (reason.Length == 0 && loose.Count > 1)
            reason = string.Join(" ", loose.Skip(1));
        return new(move, Read("CEL"), next, reason);
    }
}
