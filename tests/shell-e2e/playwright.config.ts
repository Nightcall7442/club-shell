import { defineConfig, devices } from '@playwright/test';

const baseURL = 'http://localhost:1420';
const ci = Boolean(process.env.CI);
// PW_CHANNEL=msedge|chrome drives an installed browser instead of the bundled Chromium (e.g. when it cannot start).
const channel = process.env.PW_CHANNEL;

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
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1920, height: 1080 }, ...(channel ? { channel } : {}) },
    },
  ],
  webServer: {
    command: 'pnpm --filter @clubshell/shell dev',
    url: baseURL,
    cwd: '../..',
    env: { VITE_MOCK: '1' },
    reuseExistingServer: !ci,
    timeout: 120_000,
  },
});
