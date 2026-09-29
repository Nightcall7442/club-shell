import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { defineConfig, devices } from '@playwright/test';

const baseURL = 'http://localhost:1420';
/** The admin console under test talks to its own throwaway server on :8091, never the dev one on :8080. */
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

/**
 * ADMIN_SERVER=real: the admin project runs against the real central server (docs/server/DESIGN.md §10.c) instead of the
 * mock: `dotnet run` in Development (Seed:Dev — staff 0000/1111, demo tariffs and players) on ADMIN_SERVER_DB, an Npgsql
 * connection string to a throwaway empty database the server migrates, with CORS for the console. The kiosk dev server is
 * not started (run `--project admin`). Until slice S5 the server has the counter only, so just the parts
 * "вход/карта/смена" (login/map/shift) run; S5 drops the grep.
 */
const realServer = process.env['ADMIN_SERVER'] === 'real';
const REAL_SERVER_TESTS =
  /wrong PIN|owner sees every section|cashier sees only the counter|language switch|shift opens/;
const adminDb = process.env['ADMIN_SERVER_DB'];
if (realServer && !adminDb) {
  throw new Error(
    'ADMIN_SERVER=real needs ADMIN_SERVER_DB: a connection string to an empty throwaway PostgreSQL database',
  );
}

const kioskServer = {
  command: 'pnpm --filter @clubshell/shell dev',
  url: baseURL,
  cwd: '../..',
  env: { VITE_MOCK: '1' },
  reuseExistingServer: !ci,
  timeout: 120_000,
};

const mockAdminApi = {
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
};

const realAdminApi = {
  command: `dotnet run --project server/src/ClubShell.Server --no-launch-profile --urls http://localhost:${ADMIN_API_PORT}`,
  url: `http://localhost:${ADMIN_API_PORT}/health`,
  cwd: '../..',
  env: {
    ASPNETCORE_ENVIRONMENT: 'Development',
    ConnectionStrings__Club: adminDb ?? '',
    Seed__Dev: 'true',
    Cors__AllowedOrigins__0: ADMIN_URL,
    Auth__SigningKeyPath: join(tmpdir(), `clubshell-admin-e2e-${process.pid}`, 'jwt-signing-key.pem'),
    Auth__PepperPath: join(tmpdir(), `clubshell-admin-e2e-${process.pid}`, 'pin-pepper.key'),
  },
  reuseExistingServer: false,
  timeout: 240_000,
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
    // The admin console against a real server — the mock, or with ADMIN_SERVER=real the central one — with a fresh database per run.
    {
      name: 'admin',
      testMatch: /admin\/.*\.spec\.ts$/,
      ...(realServer ? { grep: REAL_SERVER_TESTS } : {}),
      use: { ...chromium, baseURL: ADMIN_URL },
    },
  ],
  webServer: [
    ...(realServer ? [realAdminApi] : [kioskServer, mockAdminApi]),
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
