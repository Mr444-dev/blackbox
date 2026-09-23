using System.Text.Json.Serialization;

namespace Blackbox.Core.Crdt;

public enum ClockComparison
{
    Equal,
    Before,
    After,
    Concurrent
}

public class VectorClock
{
    [JsonPropertyName("versions")]
    public Dictionary<string, long> Versions { get; } = new();

    public VectorClock()
    {
    }

    public VectorClock(Dictionary<string, long> versions)
    {
        Versions = new Dictionary<string, long>(versions);
    }

    public void Increment(string nodeId)
    {
        if (Versions.ContainsKey(nodeId))
        {
            Versions[nodeId]++;
        }
        else
        {
            Versions[nodeId] = 1;
        }
    }

    public VectorClock Clone() => new(Versions);

    public VectorClock Merge(VectorClock other)
    {
        var merged = new Dictionary<string, long>(Versions);

        foreach (var (nodeId, counter) in other.Versions)
        {
            if (merged.TryGetValue(nodeId, out var existing))
            {
                merged[nodeId] = Math.Max(existing, counter);
            }
            else
            {
                merged[nodeId] = counter;
            }
        }

        return new VectorClock(merged);
    }

    public ClockComparison CompareTo(VectorClock other)
    {
        bool hasGreater = false;
        bool hasLesser = false;

        var allNodes = new HashSet<string>(Versions.Keys.Concat(other.Versions.Keys));

        foreach (var node in allNodes)
        {
            Versions.TryGetValue(node, out var val1);
            other.Versions.TryGetValue(node, out var val2);

            if (val1 > val2) hasGreater = true;
            if (val1 < val2) hasLesser = true;
        }

        if (hasGreater && hasLesser) return ClockComparison.Concurrent;
        if (hasGreater) return ClockComparison.After;
        if (hasLesser) return ClockComparison.Before;
        return ClockComparison.Equal;
    }
}
