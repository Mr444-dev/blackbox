using System.Text.Json.Serialization;

namespace Blackbox.Core.Crdt;

public class PnCounter
{
    // P-Map (Positive increments per region/node)
    [JsonPropertyName("p")]
    public Dictionary<string, long> P { get; } = new();

    // N-Map (Negative decrements per region/node)
    [JsonPropertyName("n")]
    public Dictionary<string, long> N { get; } = new();

    public PnCounter()
    {
    }

    public PnCounter(Dictionary<string, long> p, Dictionary<string, long> n)
    {
        P = new Dictionary<string, long>(p);
        N = new Dictionary<string, long>(n);
    }

    public long Value => P.Values.Sum() - N.Values.Sum();

    public void Increment(string nodeId, long value = 1)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Increment value must be positive.");

        if (P.ContainsKey(nodeId))
            P[nodeId] += value;
        else
            P[nodeId] = value;
    }

    public void Decrement(string nodeId, long value = 1)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Decrement value must be positive.");

        if (N.ContainsKey(nodeId))
            N[nodeId] += value;
        else
            N[nodeId] = value;
    }

    public PnCounter Merge(PnCounter other)
    {
        var mergedP = new Dictionary<string, long>(P);
        var mergedN = new Dictionary<string, long>(N);

        foreach (var (node, val) in other.P)
        {
            if (mergedP.TryGetValue(node, out var existing))
                mergedP[node] = Math.Max(existing, val);
            else
                mergedP[node] = val;
        }

        foreach (var (node, val) in other.N)
        {
            if (mergedN.TryGetValue(node, out var existing))
                mergedN[node] = Math.Max(existing, val);
            else
                mergedN[node] = val;
        }

        return new PnCounter(mergedP, mergedN);
    }
}
