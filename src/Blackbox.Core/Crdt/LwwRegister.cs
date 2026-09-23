namespace Blackbox.Core.Crdt;

public class LwwRegister<T>
{
    public T Value { get; private set; }
    public long TimestampTicks { get; private set; }
    public string NodeId { get; private set; }

    public LwwRegister(T initialValue, string nodeId, long? timestampTicks = null)
    {
        Value = initialValue;
        NodeId = nodeId;
        TimestampTicks = timestampTicks ?? DateTime.UtcNow.Ticks;
    }

    public void Update(T newValue, string nodeId, long? timestampTicks = null)
    {
        var newTimestamp = timestampTicks ?? DateTime.UtcNow.Ticks;

        // Deterministic Last-Write-Wins resolution with NodeId tie-breaking
        if (newTimestamp > TimestampTicks || (newTimestamp == TimestampTicks && string.CompareOrdinal(nodeId, NodeId) > 0))
        {
            Value = newValue;
            TimestampTicks = newTimestamp;
            NodeId = nodeId;
        }
    }

    public LwwRegister<T> Merge(LwwRegister<T> other)
    {
        if (other.TimestampTicks > TimestampTicks || 
           (other.TimestampTicks == TimestampTicks && string.CompareOrdinal(other.NodeId, NodeId) > 0))
        {
            return new LwwRegister<T>(other.Value, other.NodeId, other.TimestampTicks);
        }

        return new LwwRegister<T>(Value, NodeId, TimestampTicks);
    }
}
