namespace Blackbox.Core.Verification;

public record TestDataDistributionConfig
{
    public int TotalWorkflows { get; init; } = 1000;
    public double DuplicateRequestPercentage { get; init; } = 1.0; // 1%
    public double DelayedCallbackPercentage { get; init; } = 0.5;   // 0.5%
    public double MalformedPayloadPercentage { get; init; } = 0.1;  // 0.1%
    public double FailedProviderPercentage { get; init; } = 2.0;    // 2.0%
}

public record GeneratedWorkflowRecord(
    Guid Id,
    string CorrelationId,
    string PayloadJson,
    string TargetStatus,
    List<string> InjectedAnomalies,
    int AssociatedEventsCount);

public record GenerationSummary(
    int TotalGenerated,
    int NormalWorkflows,
    int DuplicateAnomalies,
    int DelayedAnomalies,
    int MalformedAnomalies,
    int FailedProviderAnomalies,
    long TotalEventsSimulated);

public class TestDataGenerator
{
    private readonly Random _random;

    public TestDataGenerator(int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random(42);
    }

    public IEnumerable<GeneratedWorkflowRecord> GenerateWorkflows(TestDataDistributionConfig config)
    {
        string? lastCorrelationId = null;

        for (int i = 0; i < config.TotalWorkflows; i++)
        {
            var anomalies = new List<string>();

            bool isDuplicate = lastCorrelationId != null && _random.NextDouble() * 100 < config.DuplicateRequestPercentage;
            bool isDelayed = _random.NextDouble() * 100 < config.DelayedCallbackPercentage;
            bool isMalformed = _random.NextDouble() * 100 < config.MalformedPayloadPercentage;
            bool isFailedProvider = _random.NextDouble() * 100 < config.FailedProviderPercentage;

            string correlationId;
            if (isDuplicate && lastCorrelationId != null)
            {
                correlationId = lastCorrelationId;
                anomalies.Add("DUPLICATE_IDEMPOTENCY_KEY");
            }
            else
            {
                correlationId = $"corr-gen-{i}-{Guid.NewGuid():N}";
                lastCorrelationId = correlationId;
            }

            string payload;
            if (isMalformed)
            {
                payload = "{\"orderId\":\"ORD-MALFORMED\",\"amount\":INVALID_JSON";
                anomalies.Add("MALFORMED_JSON_PAYLOAD");
            }
            else
            {
                payload = $"{{\"orderId\":\"ORD-{i}\",\"amount\":{_random.Next(50, 1500)}.00,\"sku\":\"SKU-GEN-{(i % 10):D3}\"}}";
            }

            string targetStatus;
            int eventCount;

            if (isMalformed)
            {
                targetStatus = "Rejected";
                eventCount = 0;
            }
            else if (isFailedProvider)
            {
                anomalies.Add("PROVIDER_503_CIRCUIT_TRIP");
                targetStatus = "Failed";
                eventCount = 3; // Created, Validated, Failed
            }
            else if (isDelayed)
            {
                anomalies.Add("DELAYED_ASYNC_CALLBACK");
                targetStatus = "WaitingCallback";
                eventCount = 4; // Created, Validated, Reserved, AwaitingCallback
            }
            else
            {
                targetStatus = "Completed";
                eventCount = 5; // Created, Validated, Reserved, Processing, Completed
            }

            yield return new GeneratedWorkflowRecord(
                Id: Guid.NewGuid(),
                CorrelationId: correlationId,
                PayloadJson: payload,
                TargetStatus: targetStatus,
                InjectedAnomalies: anomalies,
                AssociatedEventsCount: eventCount);
        }
    }

    public GenerationSummary SummarizeBatch(IEnumerable<GeneratedWorkflowRecord> records)
    {
        int total = 0;
        int duplicates = 0;
        int delayed = 0;
        int malformed = 0;
        int failedProviders = 0;
        long totalEvents = 0;

        foreach (var r in records)
        {
            total++;
            totalEvents += r.AssociatedEventsCount;

            if (r.InjectedAnomalies.Contains("DUPLICATE_IDEMPOTENCY_KEY")) duplicates++;
            if (r.InjectedAnomalies.Contains("DELAYED_ASYNC_CALLBACK")) delayed++;
            if (r.InjectedAnomalies.Contains("MALFORMED_JSON_PAYLOAD")) malformed++;
            if (r.InjectedAnomalies.Contains("PROVIDER_503_CIRCUIT_TRIP")) failedProviders++;
        }

        int normal = total - (duplicates + delayed + malformed + failedProviders);

        return new GenerationSummary(
            TotalGenerated: total,
            NormalWorkflows: normal,
            DuplicateAnomalies: duplicates,
            DelayedAnomalies: delayed,
            MalformedAnomalies: malformed,
            FailedProviderAnomalies: failedProviders,
            TotalEventsSimulated: totalEvents);
    }
}
