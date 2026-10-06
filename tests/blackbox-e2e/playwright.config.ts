import { defineConfig } from '@playwright/test';

const apiKey = process.env.BLACKBOX_API_KEY;
if (!apiKey) {
  throw new Error('Set BLACKBOX_API_KEY before running the Blackbox API tests.');
}

export default defineConfig({
  testDir: './specs',
  timeout: 30000,
  expect: {
    timeout: 5000,
  },
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: [
    ['list'],
    ['html', { outputFolder: 'playwright-report', open: 'never' }],
    ['json', { outputFile: 'test-results/results.json' }]
  ],
  use: {
    baseURL: process.env.BLACKBOX_API_URL || 'http://localhost:5000',
    extraHTTPHeaders: {
      'Accept': 'application/json',
      'Content-Type': 'application/json',
      'X-API-Key': apiKey,
    },
  },
});
