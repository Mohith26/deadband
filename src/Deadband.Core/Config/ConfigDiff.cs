using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deadband.Core.Config;

public sealed record FieldChange(string Path, string? Before, string? After);

public sealed record ConfigDiffResult(
    IReadOnlyList<string> AddedTags,
    IReadOnlyList<string> RemovedTags,
    IReadOnlyList<FieldChange> Changes)
{
    public bool IsEmpty => AddedTags.Count == 0 && RemovedTags.Count == 0 && Changes.Count == 0;
}

/// <summary>Structural diff keyed by tag id (not by array position), so reordering tags is not a change.</summary>
public static class ConfigDiff
{
    public static ConfigDiffResult Compute(AlarmConfig before, AlarmConfig after)
    {
        var a = before.Tags.ToDictionary(t => t.Tag);
        var b = after.Tags.ToDictionary(t => t.Tag);
        var added = b.Keys.Except(a.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var removed = a.Keys.Except(b.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var changes = new List<FieldChange>();
        foreach (var tag in a.Keys.Intersect(b.Keys).OrderBy(x => x, StringComparer.Ordinal))
            Walk($"tags[{tag}]", ToNode(a[tag]), ToNode(b[tag]), changes);
        foreach (var section in new[] { "multivariate", "chatter", "flood", "oee", "samplePeriodSeconds", "name", "plant" })
        {
            var na = ToNode(before)![section];
            var nb = ToNode(after)![section];
            Walk(section, na, nb, changes);
        }
        return new ConfigDiffResult(added, removed, changes);
    }

    private static JsonNode? ToNode<T>(T v) => JsonSerializer.SerializeToNode(v, AlarmConfig.Json);

    private static void Walk(string path, JsonNode? x, JsonNode? y, List<FieldChange> into)
    {
        if (x is JsonObject ox && y is JsonObject oy)
        {
            foreach (var key in ox.Select(k => k.Key).Union(oy.Select(k => k.Key)).OrderBy(k => k, StringComparer.Ordinal))
                Walk($"{path}.{key}", ox[key], oy[key], into);
            return;
        }
        var sx = x?.ToJsonString();
        var sy = y?.ToJsonString();
        if (sx != sy) into.Add(new FieldChange(path, sx, sy));
    }
}
