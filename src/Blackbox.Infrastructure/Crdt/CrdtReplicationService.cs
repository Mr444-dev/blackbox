using System.Collections.Concurrent;
using Blackbox.Core.Crdt;

namespace Blackbox.Infrastructure.Crdt;

public interface ICrdtReplicationService
{
    PnCounter MutateCounter(string resourceId, string regionId, long delta);
    PnCounter MergeCounter(string resourceId, PnCounter remoteCounter);
    PnCounter GetCounter(string resourceId);

    LwwRegister<string> MutateRegister(string resourceId, string value, string regionId, long? timestampTicks = null);
    LwwRegister<string> MergeRegister(string resourceId, LwwRegister<string> remoteRegister);
    LwwRegister<string>? GetRegister(string resourceId);
}

public class CrdtReplicationService : ICrdtReplicationService
{
    private readonly ConcurrentDictionary<string, PnCounter> _counters = new();
    private readonly ConcurrentDictionary<string, LwwRegister<string>> _registers = new();

    public PnCounter MutateCounter(string resourceId, string regionId, long delta)
    {
        return _counters.AddOrUpdate(
            resourceId,
            _ =>
            {
                var counter = new PnCounter();
                ApplyDelta(counter, regionId, delta);
                return counter;
            },
            (_, existing) =>
            {
                lock (existing)
                {
                    ApplyDelta(existing, regionId, delta);
                }
                return existing;
            });
    }

    private static void ApplyDelta(PnCounter counter, string regionId, long delta)
    {
        if (delta > 0)
        {
            counter.Increment(regionId, delta);
        }
        else if (delta < 0)
        {
            counter.Decrement(regionId, Math.Abs(delta));
        }
    }

    public PnCounter MergeCounter(string resourceId, PnCounter remoteCounter)
    {
        return _counters.AddOrUpdate(
            resourceId,
            _ => remoteCounter,
            (_, existing) =>
            {
                lock (existing)
                {
                    return existing.Merge(remoteCounter);
                }
            });
    }

    public PnCounter GetCounter(string resourceId)
    {
        return _counters.GetOrAdd(resourceId, _ => new PnCounter());
    }

    public LwwRegister<string> MutateRegister(string resourceId, string value, string regionId, long? timestampTicks = null)
    {
        return _registers.AddOrUpdate(
            resourceId,
            _ => new LwwRegister<string>(value, regionId, timestampTicks),
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Update(value, regionId, timestampTicks);
                }
                return existing;
            });
    }

    public LwwRegister<string> MergeRegister(string resourceId, LwwRegister<string> remoteRegister)
    {
        return _registers.AddOrUpdate(
            resourceId,
            _ => remoteRegister,
            (_, existing) =>
            {
                lock (existing)
                {
                    return existing.Merge(remoteRegister);
                }
            });
    }

    public LwwRegister<string>? GetRegister(string resourceId)
    {
        _registers.TryGetValue(resourceId, out var register);
        return register;
    }
}
