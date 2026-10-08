// The dashboard's end-to-end tests. `npm test` builds nothing: build the solution first (Release), then
//   cd tests/dashboard-e2e && npm ci && npx playwright install chromium && npm test
// server.js starts a stub model backend and the real `semantic-router` host on a throw-away configuration directory.
const { defineConfig, devices } = require('@playwright/test');

const port = Number(process.env.E2E_PORT || 5190);

module.exports = defineConfig({
  testDir: '.',
  testMatch: '*.spec.js',
  timeout: 30_000,
  // One router, one configuration: tests run one after another and each starts from the fixture configuration.
  workers: 1,
  fullyParallel: false,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]] : 'list',
  use: {
    ...devices['Desktop Chrome'],
    baseURL: `http://127.0.0.1:${port}`,
    viewport: { width: 1360, height: 900 },
    trace: 'retain-on-failure',
  },
  expect: {
    // Text rendering differs slightly between machines; layout or colour changes do not fit in 2 %.
    toHaveScreenshot: { maxDiffPixelRatio: 0.02, animations: 'disabled', caret: 'hide' },
  },
  snapshotPathTemplate: '{testDir}/screenshots/{arg}{ext}',
  webServer: {
    command: 'node server.js',
    url: `http://127.0.0.1:${port}/router/api/status`,
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
    stdout: 'pipe',
    stderr: 'pipe',
  },
});
