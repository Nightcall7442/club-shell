import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { defineConfig, devices } from '@playwright/test';

const baseURL = 'http://localhost:1420';
/** The admin console under test talks to its own throwaway mock server, never the dev one on :8080. */
const ADMIN_URL = 'http://localhost:1431';
const ADMIN_API_PORT = 8091;
const ci = Boolean(process.env.CI);
// PW_CHANNEL=msedge|chrome drives an installed browser instead of the bundled Chromium (e.g. when it cannot start).
const channel = process.env.PW_CHANNEL;
const chromium = {
  ...devices['Desktop Chrome'],
  viewport: { width: 1920, height: 1080 },
  ...(channel ? { channel } : {}),
};

export default defineConfig({
  testDir: '.',
  testMatch: /.*\.spec\.ts$/,
  timeout: 30_000,
  expect: { timeout: 5_000 },
  fullyParallel: false,
  workers: 1,
  forbidOnly: ci,
  retries: ci ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  outputDir: 'test-results',
  use: {
    baseURL,
    viewport: { width: 1920, height: 1080 },
    video: 'retain-on-failure',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    locale: 'en-US',
    timezoneId: 'Asia/Tashkent',
  },
  projects: [
    // The kiosk in mock mode: every Tauri command is answered in the browser.
    { name: 'chromium', testIgnore: /admin\//, use: chromium },
    // The admin console against a real (mock) server with a fresh database per run.
    { name: 'admin', testMatch: /admin\/.*\.spec\.ts$/, use: { ...chromium, baseURL: ADMIN_URL } },
  ],
  webServer: [
    {
      command: 'pnpm --filter @clubshell/shell dev',
      url: baseURL,
      cwd: '../..',
      env: { VITE_MOCK: '1' },
      reuseExistingServer: !ci,
      timeout: 120_000,
    },
    {
      command: 'pnpm --filter @clubshell/mock-server exec tsx src/index.ts --reset',
      url: `http://localhost:${ADMIN_API_PORT}/health`,
      cwd: '../..',
      env: {
        MOCK_SERVER_PORT: String(ADMIN_API_PORT),
        MOCK_PUBLIC_URL: `http://localhost:${ADMIN_API_PORT}`,
        MOCK_DB_FILE: join(tmpdir(), `clubshell-admin-e2e-${process.pid}.json`),
      },
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      command: 'pnpm --filter @clubshell/admin exec vite --port 1431 --strictPort',
      url: ADMIN_URL,
      cwd: '../..',
      env: { VITE_ADMIN_API: `http://localhost:${ADMIN_API_PORT}/api/v1` },
      reuseExistingServer: false,
      timeout: 120_000,
    },
  ],
});
