import { test, expect } from '@playwright/test';

test.describe('Blackbox Scenario Verification Scorecard', () => {

  test('POST /api/v1/scenarios/run reports evaluated checks and missing evidence', async ({ request }) => {
    const scenarioRequest = {
      scenarioName: 'E2E_Automated_BlackboxGuarantee_Spec',
      correlationId: `E2E-GUARANTEE-${Date.now()}`,
      payloadJson: '{"orderId":"ORD-GUARANTEE-01","total":450.00}',
      simulateFailure: false,
      simulatedDurationMs: 85.0,
      callerIdentity: 'qa_playwright_test_engine',
      isAuthenticated: true,
      hasValidRole: true
    };

    const response = await request.post('/api/v1/scenarios/run', {
      data: scenarioRequest
    });

    expect(response.status()).toBe(200);
    const report = await response.json();

    // Ten checks are represented; checks without supplied evidence are marked not evaluated.
    expect(report.pillars).toHaveLength(10);
    expect(report.overallPassed).toBe(true);
    expect(report.totalViolations).toBe(0);

    // The summary reports measured scenario evidence and the number of evaluated pillars.
    expect(report.signatureSummary).toMatch(/^PASS:.*\|\s+\d+\s+invariants/);
    expect(report.signatureSummary).toContain('0 violations');
  });

  test('POST /api/v1/scenarios/run with unauthenticated caller detects security violation', async ({ request }) => {
    const scenarioRequest = {
      scenarioName: 'Security_Violation_Probe',
      correlationId: `E2E-SEC-FAIL-${Date.now()}`,
      payloadJson: '{"orderId":"ORD-SEC-02"}',
      simulateFailure: false,
      callerIdentity: 'unauthorized_attacker',
      isAuthenticated: false, // Breach!
      hasValidRole: false
    };

    const response = await request.post('/api/v1/scenarios/run', {
      data: scenarioRequest
    });

    expect(response.status()).toBe(200);
    const report = await response.json();

    expect(report.overallPassed).toBe(false);
    expect(report.totalViolations).toBeGreaterThan(0);
    expect(report.signatureSummary).toMatch(/^FAIL:/);

    const secPillar = report.pillars.find((p: any) => p.pillarName === 'Security Scenario Checks');
    expect(secPillar.passed).toBe(false);
  });
});
