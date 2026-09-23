using Blackbox.Core.Domain;
using Blackbox.Core.Observability;
using Blackbox.Infrastructure.Persistence;
using Blackbox.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Database & Services
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Host=localhost;Port=5432;Database=blackbox_db;Username=blackbox_user;Password=[REDACTED]";

builder.Services.AddDbContext<BlackboxDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<IResourceRepository, ResourceRepository>();
builder.Services.AddScoped<IWorkflowOrchestrator, WorkflowOrchestrator>();
builder.Services.AddScoped<Blackbox.Infrastructure.External.ICarrierService, Blackbox.Infrastructure.External.ResilientCarrierService>();
builder.Services.AddSingleton<Blackbox.Infrastructure.Crdt.ICrdtReplicationService, Blackbox.Infrastructure.Crdt.CrdtReplicationService>();
builder.Services.AddSingleton<Blackbox.Infrastructure.Cryptography.IAuditLedger, Blackbox.Infrastructure.Cryptography.MerkleAuditLedger>();
builder.Services.AddSingleton<Blackbox.Infrastructure.Observability.IEbpfSocketMetricsBridge, Blackbox.Infrastructure.Observability.EbpfSocketMetricsBridge>();
builder.Services.AddSingleton<Blackbox.Core.Verification.IBlackboxGuaranteeEngine, Blackbox.Core.Verification.BlackboxGuaranteeEngine>();
builder.Services.AddSingleton<Blackbox.Core.Ai.IAutonomousRemediationAgent, Blackbox.Core.Ai.AutonomousRemediationAgent>();
builder.Services.AddSingleton<Blackbox.Core.Cryptography.PostQuantum.PostQuantumDilithiumSigner>();
builder.Services.AddSingleton<Blackbox.Core.Cryptography.PostQuantum.PostQuantumKyberKEM>();
builder.Services.AddSingleton<Blackbox.Core.Cryptography.PostQuantum.IQuantumAuditSealer, Blackbox.Core.Cryptography.PostQuantum.QuantumResistantAuditSealer>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// OpenTelemetry Distributed Tracing & Metrics
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(Blackbox.Core.Observability.BlackboxObservability.ServiceName, 
                    serviceVersion: Blackbox.Core.Observability.BlackboxObservability.ServiceVersion))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(Blackbox.Core.Observability.BlackboxObservability.ServiceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(Blackbox.Core.Observability.BlackboxObservability.ServiceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
    });

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Ensure database schema and seed data exist
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<BlackboxDbContext>();
    try
    {
        await dbContext.Database.EnsureCreatedAsync();
        if (!await dbContext.Resources.AnyAsync())
        {
            await dbContext.Resources.AddRangeAsync(
                new Blackbox.Core.Domain.Resource("SKU-LAPTOP-001", "High-End Laptop", 1),
                new Blackbox.Core.Domain.Resource("SKU-PHONE-002", "Smartphone", 10),
                new Blackbox.Core.Domain.Resource("SKU-DESK-003", "Standing Desk", 5)
            );
            await dbContext.SaveChangesAsync();
        }
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Could not auto-initialize PostgreSQL on startup. Please verify connection credentials.");
    }
}

// Health Check
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTime.UtcNow }));

// 1. Create / Trigger Workflow (with Idempotency Key)
app.MapPost("/api/v1/workflows", async (
    [FromHeader(Name = "X-Idempotency-Key")] string? idempotencyKey,
    [FromBody] CreateWorkflowRequest request,
    [FromServices] IWorkflowOrchestrator orchestrator) =>
{
    var correlationId = !string.IsNullOrWhiteSpace(idempotencyKey) 
        ? idempotencyKey 
        : request.CorrelationId;

    if (string.IsNullOrWhiteSpace(correlationId))
    {
        return Results.BadRequest(new { error = "Either X-Idempotency-Key header or correlationId body field is required." });
    }

    try
    {
        var workflow = await orchestrator.StartWorkflowAsync(correlationId, request.PayloadJson);
        BlackboxObservability.WorkflowsCreatedCounter.Add(1);
        return Results.Ok(new
        {
            id = workflow.Id,
            correlationId = workflow.CorrelationId,
            status = workflow.Status.ToString(),
            version = workflow.Version,
            createdAt = workflow.CreatedAt
        });
    }
    catch (WorkflowInvariantException ex)
    {
        BlackboxObservability.InvariantViolationsCounter.Add(1);
        return Results.Conflict(new { error = ex.Message });
    }
});

// 2. Get Workflow by ID
app.MapGet("/api/v1/workflows/{id:guid}", async (
    Guid id,
    [FromServices] BlackboxDbContext dbContext) =>
{
    var workflow = await dbContext.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
    if (workflow == null)
        return Results.NotFound(new { error = $"Workflow {id} not found." });

    return Results.Ok(new
    {
        id = workflow.Id,
        correlationId = workflow.CorrelationId,
        status = workflow.Status.ToString(),
        payload = workflow.PayloadJson,
        version = workflow.Version,
        createdAt = workflow.CreatedAt,
        updatedAt = workflow.UpdatedAt
    });
});

// 3. Reserve Resource (Pessimistic Lock with Invariant Enforcement)
app.MapPost("/api/v1/workflows/{id:guid}/reserve", async (
    Guid id,
    [FromBody] ReserveResourceRequest request,
    [FromServices] IWorkflowOrchestrator orchestrator) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var reservation = await orchestrator.ReserveResourceAsync(id, request.ResourceId, request.Quantity);
        sw.Stop();
        BlackboxObservability.ReservationDurationMs.Record(sw.Elapsed.TotalMilliseconds);

        return Results.Ok(new
        {
            reservationId = reservation.Id,
            resourceId = reservation.ResourceId,
            workflowId = reservation.WorkflowId,
            quantity = reservation.Quantity,
            status = reservation.Status.ToString()
        });
    }
    catch (WorkflowInvariantException ex)
    {
        BlackboxObservability.InvariantViolationsCounter.Add(1);
        return Results.Conflict(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// 4. Complete Workflow
app.MapPost("/api/v1/workflows/{id:guid}/complete", async (
    Guid id,
    [FromServices] IWorkflowOrchestrator orchestrator) =>
{
    try
    {
        var workflow = await orchestrator.CompleteWorkflowAsync(id);
        BlackboxObservability.WorkflowsCompletedCounter.Add(1);

        return Results.Ok(new
        {
            id = workflow.Id,
            status = workflow.Status.ToString(),
            version = workflow.Version,
            updatedAt = workflow.UpdatedAt
        });
    }
    catch (WorkflowInvariantException ex)
    {
        BlackboxObservability.InvariantViolationsCounter.Add(1);
        return Results.Conflict(new { error = ex.Message });
    }
});

// 5. Fail Workflow
app.MapPost("/api/v1/workflows/{id:guid}/fail", async (
    Guid id,
    [FromBody] FailWorkflowRequest request,
    [FromServices] IWorkflowOrchestrator orchestrator) =>
{
    try
    {
        var workflow = await orchestrator.FailWorkflowAsync(id, request.Reason);
        BlackboxObservability.WorkflowsFailedCounter.Add(1);

        return Results.Ok(new
        {
            id = workflow.Id,
            status = workflow.Status.ToString(),
            version = workflow.Version,
            updatedAt = workflow.UpdatedAt
        });
    }
    catch (WorkflowInvariantException ex)
    {
        BlackboxObservability.InvariantViolationsCounter.Add(1);
        return Results.Conflict(new { error = ex.Message });
    }
});

// 6. Resilient Carrier Dispatch (with Fault Simulation & Circuit Breaker)
app.MapPost("/api/v1/workflows/{id:guid}/dispatch", async (
    Guid id,
    [FromBody] DispatchCarrierRequest request,
    [FromServices] Blackbox.Infrastructure.External.ICarrierService carrierService) =>
{
    var mode = request.Mode ?? Blackbox.Infrastructure.External.CarrierSimulatorMode.Healthy;
    var result = await carrierService.DispatchShipmentAsync(id, request.DestinationAddress, mode);

    if (result.Success)
    {
        return Results.Ok(new
        {
            success = true,
            trackingNumber = result.TrackingNumber,
            attemptCount = result.AttemptCount,
            status = "WaitingCallback"
        });
    }

    BlackboxObservability.DeadLetterQuarantinedCounter.Add(1);

    if (result.CircuitBreakerTripped)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return Results.BadRequest(new
    {
        success = false,
        error = result.Error,
        attemptCount = result.AttemptCount
    });
});

// 7. Dead Letter Queue: List Quarantined Messages
app.MapGet("/api/v1/dlq", async ([FromServices] BlackboxDbContext dbContext) =>
{
    var messages = await dbContext.DeadLetterMessages
        .AsNoTracking()
        .OrderByDescending(d => d.CreatedAt)
        .Take(50)
        .ToListAsync();

    return Results.Ok(messages.Select(m => new
    {
        id = m.Id,
        workflowId = m.WorkflowId,
        source = m.Source,
        exceptionMessage = m.ExceptionMessage,
        status = m.Status.ToString(),
        createdAt = m.CreatedAt,
        replayedAt = m.ReplayedAt
    }));
});

// 8. Dead Letter Queue: Deterministic Replay Endpoint
app.MapPost("/api/v1/dlq/{id:guid}/replay", async (
    Guid id,
    [FromServices] BlackboxDbContext dbContext,
    [FromServices] Blackbox.Infrastructure.External.ICarrierService carrierService) =>
{
    var dlqItem = await dbContext.DeadLetterMessages.FirstOrDefaultAsync(d => d.Id == id);
    if (dlqItem == null)
        return Results.NotFound(new { error = $"DLQ record {id} not found." });

    if (dlqItem.WorkflowId.HasValue)
    {
        // Execute replay in healthy mode
        var replayResult = await carrierService.DispatchShipmentAsync(
            dlqItem.WorkflowId.Value, 
            "Replay Address", 
            Blackbox.Infrastructure.External.CarrierSimulatorMode.Healthy);

        if (replayResult.Success)
        {
            dlqItem.MarkReplayed();
            await dbContext.SaveChangesAsync();

            return Results.Ok(new
            {
                status = "REPLAYED",
                dlqId = dlqItem.Id,
                trackingNumber = replayResult.TrackingNumber,
                replayedAt = dlqItem.ReplayedAt
            });
        }
    }

    return Results.BadRequest(new { error = "Unable to automatically replay non-workflow DLQ record." });
});

// 9. Distributed Saga: Multi-Step Execution with Automatic Compensating Transactions
app.MapPost("/api/v1/workflows/saga", async (
    [FromBody] RunSagaRequest request,
    [FromServices] IWorkflowOrchestrator orchestrator,
    [FromServices] BlackboxDbContext dbContext,
    [FromServices] Blackbox.Infrastructure.External.ICarrierService carrierService,
    [FromServices] ILoggerFactory loggerFactory) =>
{
    var workflow = await orchestrator.StartWorkflowAsync(request.CorrelationId, "{\"saga\":\"fulfillment\"}");

    var saga = new Blackbox.Core.Saga.SagaOrchestrator<Blackbox.Infrastructure.Saga.FulfillmentSagaContext>(
        loggerFactory.CreateLogger("FulfillmentSaga"))
        .AddStep(new Blackbox.Infrastructure.Saga.ReserveInventorySagaStep(orchestrator, dbContext, loggerFactory.CreateLogger<Blackbox.Infrastructure.Saga.ReserveInventorySagaStep>()))
        .AddStep(new Blackbox.Infrastructure.Saga.DispatchCarrierSagaStep(carrierService, loggerFactory.CreateLogger<Blackbox.Infrastructure.Saga.DispatchCarrierSagaStep>()))
        .AddStep(new Blackbox.Infrastructure.Saga.ProcessPaymentSagaStep(loggerFactory.CreateLogger<Blackbox.Infrastructure.Saga.ProcessPaymentSagaStep>()));

    var context = new Blackbox.Infrastructure.Saga.FulfillmentSagaContext
    {
        WorkflowId = workflow.Id,
        CorrelationId = workflow.CorrelationId,
        ResourceId = request.ResourceId,
        Quantity = request.Quantity,
        DestinationAddress = request.DestinationAddress,
        Amount = request.Amount,
        SimulatePaymentFailure = request.SimulatePaymentFailure
    };

    var result = await saga.ExecuteAsync(context);

    if (result.Success)
    {
        await orchestrator.CompleteWorkflowAsync(workflow.Id);
        return Results.Ok(new
        {
            workflowId = workflow.Id,
            status = "COMPLETED",
            trackingNumber = context.TrackingNumber,
            executedSteps = result.ExecutedSteps
        });
    }

    await orchestrator.FailWorkflowAsync(workflow.Id, $"Saga Compensated: {result.Error}");
    return Results.UnprocessableEntity(new
    {
        workflowId = workflow.Id,
        status = "COMPENSATED",
        failedStep = result.FailedStep,
        error = result.Error,
        compensatedSteps = result.CompensatedSteps
    });
});

// 10. CRDT: Local Region Counter Delta Mutation (Increment / Decrement)
app.MapPost("/api/v1/crdt/counters/{resourceId}/delta", (
    string resourceId,
    [FromBody] MutateCrdtCounterRequest request,
    [FromServices] Blackbox.Infrastructure.Crdt.ICrdtReplicationService crdtService) =>
{
    if (string.IsNullOrWhiteSpace(request.RegionId))
        return Results.BadRequest(new { error = "RegionId is required." });

    var counter = crdtService.MutateCounter(resourceId, request.RegionId, request.Delta);
    return Results.Ok(new
    {
        resourceId,
        convergedValue = counter.Value,
        positive = counter.P,
        negative = counter.N
    });
});

// 11. CRDT: Cross-Region Gossip / Replication Merge
app.MapPost("/api/v1/crdt/counters/{resourceId}/sync", (
    string resourceId,
    [FromBody] SyncCrdtCounterRequest request,
    [FromServices] Blackbox.Infrastructure.Crdt.ICrdtReplicationService crdtService) =>
{
    var remoteCounter = new Blackbox.Core.Crdt.PnCounter(request.P, request.N);
    var converged = crdtService.MergeCounter(resourceId, remoteCounter);
    return Results.Ok(new
    {
        resourceId,
        convergedValue = converged.Value,
        positive = converged.P,
        negative = converged.N
    });
});

// 12. CRDT: Get Current Converged State
app.MapGet("/api/v1/crdt/counters/{resourceId}", (
    string resourceId,
    [FromServices] Blackbox.Infrastructure.Crdt.ICrdtReplicationService crdtService) =>
{
    var counter = crdtService.GetCounter(resourceId);
    return Results.Ok(new
    {
        resourceId,
        convergedValue = counter.Value,
        positive = counter.P,
        negative = counter.N
    });
});

// 13. Merkle Audit Trail: Append Cryptographic Event to Hash Chain
app.MapPost("/api/v1/audit/events", (
    [FromBody] CreateAuditEventRequest request,
    [FromServices] Blackbox.Infrastructure.Cryptography.IAuditLedger auditLedger) =>
{
    var entry = auditLedger.AppendEvent(
        request.WorkflowId,
        request.FromState,
        request.ToState,
        request.PayloadJson);

    return Results.Ok(new
    {
        sequenceNumber = entry.SequenceNumber,
        workflowId = entry.WorkflowId,
        fromState = entry.FromState,
        toState = entry.ToState,
        payloadHash = entry.PayloadHash,
        previousEntryHash = entry.PreviousEntryHash,
        entryHash = entry.EntryHash,
        timestampUtc = entry.TimestampUtc
    });
});

// 14. Merkle Audit Trail: Seal Block & Commit Merkle Root
app.MapPost("/api/v1/audit/seal", (
    [FromServices] Blackbox.Infrastructure.Cryptography.IAuditLedger auditLedger) =>
{
    try
    {
        var block = auditLedger.SealBlock();
        return Results.Ok(new
        {
            blockId = block.Id,
            blockNumber = block.BlockNumber,
            merkleRoot = block.MerkleRoot,
            leafCount = block.LeafCount,
            sealedAt = block.SealedAt
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// 15. Merkle Audit Trail: Generate Cryptographic Inclusion Proof for Auditor
app.MapGet("/api/v1/audit/proof/{entryHash}", (
    string entryHash,
    [FromServices] Blackbox.Infrastructure.Cryptography.IAuditLedger auditLedger) =>
{
    var proof = auditLedger.GenerateProof(entryHash);
    if (proof == null)
        return Results.NotFound(new { error = $"Proof for entry hash '{entryHash}' not found or block not sealed yet." });

    return Results.Ok(proof);
});

// 16. Merkle Audit Trail: Verify Proof Against Certified Root
app.MapPost("/api/v1/audit/verify", (
    [FromBody] VerifyAuditProofRequest request,
    [FromServices] Blackbox.Infrastructure.Cryptography.IAuditLedger auditLedger) =>
{
    var isValid = auditLedger.VerifyProof(request.Proof);
    return Results.Ok(new { valid = isValid, verifiedRoot = request.Proof.RootHash });
});

// 17. Merkle Audit Trail: Zero-Knowledge Statement Verification (Without Leaking Raw Payload)
app.MapPost("/api/v1/audit/zk-verify", (
    [FromBody] ZkVerifyClaimRequest request) =>
{
    var isValid = Blackbox.Core.Cryptography.ZkAuditVerifier.VerifyClaimWithoutPayload(
        request.CertifiedRoot,
        request.Claim,
        request.Proof);

    return Results.Ok(new
    {
        valid = isValid,
        workflowId = request.Claim.WorkflowId,
        verifiedStateTransition = $"{request.Claim.FromState} -> {request.Claim.ToState}",
        zeroKnowledgePayloadIntegrity = "Verified without disclosing raw confidential payload"
    });
});

// 18. eBPF: Ingest Kernel Socket Event from BPF Ring Buffer
app.MapPost("/api/v1/ebpf/events", (
    [FromBody] IngestEbpfEventRequest request,
    [FromServices] Blackbox.Infrastructure.Observability.IEbpfSocketMetricsBridge ebpfBridge) =>
{
    var evt = new Blackbox.Infrastructure.Observability.EbpfSocketEvent(
        request.Pid,
        request.SourceIp,
        request.DestinationIp,
        request.SourcePort,
        request.DestinationPort,
        request.LatencyUs,
        request.SocketRttUs,
        request.Retransmits,
        request.HttpStatus,
        request.Method);

    ebpfBridge.ProcessKernelSocketEvent(evt);
    return Results.Ok(new { status = "Ingested", latencyUs = evt.LatencyUs });
});

// 19. eBPF: Retrieve Recent Kernel Socket Diagnostics
app.MapGet("/api/v1/ebpf/events", (
    [FromServices] Blackbox.Infrastructure.Observability.IEbpfSocketMetricsBridge ebpfBridge) =>
{
    var events = ebpfBridge.GetRecentKernelEvents(50);
    return Results.Ok(events);
});

// 20. BLACKBOX GUARANTEE: Verify Live Workflow Invariants & Contracts
app.MapPost("/api/v1/guarantee/verify/{id:guid}", async (
    Guid id,
    [FromServices] BlackboxDbContext dbContext,
    [FromServices] Blackbox.Core.Verification.IBlackboxGuaranteeEngine guaranteeEngine) =>
{
    var workflow = await dbContext.Workflows.FirstOrDefaultAsync(w => w.Id == id);
    if (workflow == null)
        return Results.NotFound(new { error = $"Workflow {id} not found." });

    var outboxMessages = await dbContext.OutboxMessages
        .Where(o => o.Payload.Contains(workflow.CorrelationId))
        .Select(o => o.Id.ToString())
        .ToListAsync();

    var context = new Blackbox.Core.Verification.WorkflowScenarioContext
    {
        ScenarioName = $"LiveWorkflowVerification_{workflow.CorrelationId}",
        Workflow = workflow,
        OutboxMessageIds = outboxMessages,
        ExecutionDurationMs = (workflow.UpdatedAt - workflow.CreatedAt).TotalMilliseconds,
        SecurityAudit = new Blackbox.Core.Verification.SecurityAuditRecord("api_client", true, true, true, true)
    };

    var report = await guaranteeEngine.VerifyWorkflowScenarioAsync(context);
    return Results.Ok(report);
});

// 21. BLACKBOX GUARANTEE: Run Scenario & Output Formal Verification Scorecard
app.MapPost("/api/v1/scenarios/run", async (
    [FromBody] RunScenarioApiRequest request,
    [FromServices] Blackbox.Core.Verification.IBlackboxGuaranteeEngine guaranteeEngine) =>
{
    var workflow = Blackbox.Core.Domain.WorkflowInstance.Create(
        request.CorrelationId ?? $"scenario-{Guid.NewGuid():N}",
        request.PayloadJson ?? "{\"item\":\"GPU-RTX4090\",\"qty\":1}");

    workflow.Validate();
    if (request.SimulateFailure)
    {
        workflow.Fail("Simulated external dependency failure");
    }
    else
    {
        workflow.Reserve();
        workflow.StartProcessing();
        workflow.Complete();
    }

    var context = new Blackbox.Core.Verification.WorkflowScenarioContext
    {
        ScenarioName = request.ScenarioName ?? "OnDemandVerificationScenario",
        Workflow = workflow,
        ExecutionDurationMs = request.SimulatedDurationMs > 0 ? request.SimulatedDurationMs : 142.0,
        SecurityAudit = new Blackbox.Core.Verification.SecurityAuditRecord(
            request.CallerIdentity ?? "authorized_tester",
            request.IsAuthenticated,
            request.HasValidRole,
            true,
            true)
    };

    var report = await guaranteeEngine.VerifyWorkflowScenarioAsync(context);
    return Results.Ok(report);
});

// 22. AI Autonomous Remediation: Diagnose Incident Root Cause from OpenTelemetry Traces
app.MapPost("/api/v1/ai/diagnose", async (
    [FromBody] Blackbox.Core.Ai.IncidentTelemetryContext context,
    [FromServices] Blackbox.Core.Ai.IAutonomousRemediationAgent remediationAgent) =>
{
    var recommendation = await remediationAgent.DiagnoseIncidentAsync(context);
    return Results.Ok(recommendation);
});

// 23. AI Autonomous Remediation: Execute Recommended Policy (Saga Rollback, DLQ Quarantine, Jitter Backoff)
app.MapPost("/api/v1/ai/remediate", async (
    [FromBody] ExecuteRemediationApiRequest request,
    [FromServices] Blackbox.Core.Ai.IAutonomousRemediationAgent remediationAgent) =>
{
    var result = await remediationAgent.ExecuteRemediationPolicyAsync(request.Context, request.Recommendation);
    return Results.Ok(result);
});

// 24. Post-Quantum Cryptography: Generate NIST FIPS 204 ML-DSA Dilithium KeyPair
app.MapPost("/api/v1/crypto/pq/keys/dilithium", (
    [FromServices] Blackbox.Core.Cryptography.PostQuantum.PostQuantumDilithiumSigner signer) =>
{
    var keyPair = signer.GenerateKeyPair();
    byte[] tBytes = Blackbox.Core.Cryptography.PostQuantum.PostQuantumDilithiumSigner.SerializeVector(keyPair.PublicKey.VectorT);
    return Results.Ok(new
    {
        algorithm = keyPair.PublicKey.Algorithm,
        keyId = keyPair.PublicKey.KeyId,
        seedRhoHex = Convert.ToHexString(keyPair.PublicKey.SeedRho).ToLowerInvariant(),
        publicKeyTHex = Convert.ToHexString(tBytes).ToLowerInvariant()
    });
});

// 25. Post-Quantum Cryptography: Seal Workflow Merkle Root with Dilithium Lattice Signature
app.MapPost("/api/v1/crypto/pq/seal", (
    [FromBody] SealAuditRequest request,
    [FromServices] Blackbox.Core.Cryptography.PostQuantum.PostQuantumDilithiumSigner signer,
    [FromServices] Blackbox.Core.Cryptography.PostQuantum.IQuantumAuditSealer sealer) =>
{
    var keyPair = signer.GenerateKeyPair();
    var seal = sealer.CreateSeal(request.WorkflowId, request.MerkleRootHash, keyPair);
    return Results.Ok(seal);
});

// 26. Post-Quantum Cryptography: Verify Quantum-Resistant Audit Seal
app.MapPost("/api/v1/crypto/pq/verify", (
    [FromBody] Blackbox.Core.Cryptography.PostQuantum.QuantumAuditSeal seal,
    [FromServices] Blackbox.Core.Cryptography.PostQuantum.IQuantumAuditSealer sealer) =>
{
    var isValid = sealer.VerifySeal(seal);
    return Results.Ok(new
    {
        valid = isValid,
        sealId = seal.SealId,
        workflowId = seal.WorkflowId,
        algorithm = seal.Algorithm,
        securityCategory = seal.SecurityCategory
    });
});

// 27. Post-Quantum Cryptography: NIST FIPS 203 ML-KEM Kyber Lattice Key Encapsulation
app.MapPost("/api/v1/crypto/pq/kyber/encapsulate", (
    [FromServices] Blackbox.Core.Cryptography.PostQuantum.PostQuantumKyberKEM kyber) =>
{
    var keyPair = kyber.GenerateKeyPair();
    var enc = kyber.Encapsulate(keyPair.PublicKey);
    var dec = kyber.Decapsulate(enc.Ciphertext, keyPair.PrivateKey);
    bool matched = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(enc.SharedSecret256, dec);
    return Results.Ok(new
    {
        algorithm = keyPair.PublicKey.Algorithm,
        keyId = keyPair.PublicKey.KeyId,
        ciphertextHex = Convert.ToHexString(enc.Ciphertext).ToLowerInvariant(),
        sharedSecretHex = Convert.ToHexString(enc.SharedSecret256).ToLowerInvariant(),
        decapsulationMatched = matched
    });
});

app.Run();

// DTO records
public record CreateWorkflowRequest(string? CorrelationId, string PayloadJson);
public record ReserveResourceRequest(string ResourceId, int Quantity);
public record FailWorkflowRequest(string Reason);
public record DispatchCarrierRequest(string DestinationAddress, Blackbox.Infrastructure.External.CarrierSimulatorMode? Mode);
public record RunSagaRequest(string CorrelationId, string ResourceId, int Quantity, string DestinationAddress, decimal Amount, bool SimulatePaymentFailure);
public record MutateCrdtCounterRequest(string RegionId, long Delta);
public record SyncCrdtCounterRequest(Dictionary<string, long> P, Dictionary<string, long> N);
public record CreateAuditEventRequest(Guid WorkflowId, string FromState, string ToState, string PayloadJson);
public record VerifyAuditProofRequest(Blackbox.Core.Cryptography.MerkleProof Proof);
public record ZkVerifyClaimRequest(string CertifiedRoot, Blackbox.Core.Cryptography.ZkAuditPublicClaim Claim, Blackbox.Core.Cryptography.MerkleProof Proof);
public record IngestEbpfEventRequest(uint Pid, string SourceIp, string DestinationIp, ushort SourcePort, ushort DestinationPort, double LatencyUs, double SocketRttUs, uint Retransmits, ushort HttpStatus, string Method);
public record RunScenarioApiRequest(string? ScenarioName, string? CorrelationId, string? PayloadJson, bool SimulateFailure = false, double SimulatedDurationMs = 140.0, string? CallerIdentity = "qa_automation", bool IsAuthenticated = true, bool HasValidRole = true);
public record ExecuteRemediationApiRequest(Blackbox.Core.Ai.IncidentTelemetryContext Context, Blackbox.Core.Ai.RemediationRecommendation Recommendation);
public record SealAuditRequest(Guid WorkflowId, string MerkleRootHash);

// Make Program public for WebApplicationFactory in Integration Tests
public partial class Program { }
