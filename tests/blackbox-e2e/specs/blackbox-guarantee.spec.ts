import { test, expect } from '@playwright/test';

test.describe('BLACKBOX GUARANTEE Formal Verification Engine', () => {

  test('POST /api/v1/scenarios/run returns PASS with all 10 verification pillars intact', async ({ request }) => {
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

    // Verify 10 formal pillars
    expect(report.pillars).toHaveLength(10);
    expect(report.overallPassed).toBe(true);
    expect(report.totalViolations).toBe(0);

    // Verify signature formal output format
    // e.g. "PASS: 13 invariants | 48 assertions | ... | 0 violations"
    expect(report.signatureSummary).toMatch(/^PASS:\s+\d+\s+invariants/);
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

    const secPillar = report.pillars.find((p: any) => p.pillarName === 'Security Rules');
    expect(secPillar.passed).toBe(false);
  });
});
