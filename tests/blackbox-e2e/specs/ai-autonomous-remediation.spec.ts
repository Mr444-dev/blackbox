import { test, expect } from '@playwright/test';

test.describe('AI-Driven Autonomous Remediation Engine', () => {

  test('POST /api/v1/ai/diagnose analyzes payment settlement failure and recommends saga rollback', async ({ request }) => {
    const incidentContext = {
      incidentId: `INC-AI-E2E-${Date.now()}`,
      workflowId: 'e2e00000-0000-0000-0000-000000000101',
      correlationId: `corr-ai-e2e-${Date.now()}`,
      failedStep: 'ProcessPayment',
      exceptionMessage: 'Payment capture failed: InsufficientFunds downstream',
      traceSpans: [
        { traceId: '4bf92f3577b34da6a3ce929d0e0e4736', spanId: 's1', parentSpanId: null, serviceName: 'Blackbox.Api', operationName: 'POST /saga', durationMs: 20 },
        { traceId: '4bf92f3577b34da6a3ce929d0e0e4736', spanId: 's2', parentSpanId: 's1', serviceName: 'Blackbox.Worker', operationName: 'ReserveInventory', durationMs: 35 },
        { traceId: '4bf92f3577b34da6a3ce929d0e0e4736', spanId: 's3', parentSpanId: 's1', serviceName: 'External.Payment', operationName: 'ChargeCard', durationMs: 280 }
      ],
      historicalTransitions: ['Created', 'Validated', 'Reserved', 'Failed']
    };

    // 1. Diagnose Root Cause
    const diagResponse = await request.post('/api/v1/ai/diagnose', {
      data: incidentContext
    });

    expect(diagResponse.status()).toBe(200);
    const recommendation = await diagResponse.json();

    expect(recommendation.incidentId).toBe(incidentContext.incidentId);
    expect(recommendation.recommendedAction).toBe('RollbackSaga');
    expect(recommendation.confidenceScore).toBeGreaterThan(0.90);
    expect(recommendation.requiresHumanApproval).toBe(false);
    expect(recommendation.suggestedPolicyJson).toContain('REVERSE_SAGA_COMPENSATION');

    // 2. Execute Recommended Remediation Policy Autonomously
    const remResponse = await request.post('/api/v1/ai/remediate', {
      data: {
        context: incidentContext,
        recommendation: recommendation
      }
    });

    expect(remResponse.status()).toBe(200);
    const executionResult = await remResponse.json();

    expect(executionResult.success).toBe(true);
    expect(executionResult.stateAfterRemediation).toBe('COMPENSATED');
    expect(executionResult.executionNotes).toContain('Invariants preserved');
  });

  test('POST /api/v1/ai/remediate enforces human approval guardrail for unknown anomalies', async ({ request }) => {
    const unknownIncident = {
      incidentId: `INC-AI-UNKNOWN-${Date.now()}`,
      workflowId: 'e2e00000-0000-0000-0000-000000000102',
      correlationId: `corr-ai-guardrail-${Date.now()}`,
      failedStep: 'KernelDriverStep',
      exceptionMessage: 'Unknown proprietary device failure code 0xFF99',
      traceSpans: [],
      historicalTransitions: ['Created']
    };

    const diagResponse = await request.post('/api/v1/ai/diagnose', {
      data: unknownIncident
    });
    const recommendation = await diagResponse.json();

    expect(recommendation.recommendedAction).toBe('EscalateToHuman');
    expect(recommendation.requiresHumanApproval).toBe(true);

    // Attempting execution must be blocked
    const remResponse = await request.post('/api/v1/ai/remediate', {
      data: {
        context: unknownIncident,
        recommendation: recommendation
      }
    });

    const executionResult = await remResponse.json();
    expect(executionResult.success).toBe(false);
    expect(executionResult.stateAfterRemediation).toBe('PENDING_HUMAN_APPROVAL');
    expect(executionResult.executionNotes).toContain('prevented by security guardrail');
  });
});
