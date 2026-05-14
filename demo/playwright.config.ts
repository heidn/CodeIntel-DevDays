import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: '*.demo.ts',
  timeout: 10 * 60 * 1000,       // 10 min — analysis + trace can take a while
  expect: { timeout: 30_000 },
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:5000',
    screenshot: 'on',
    video: 'retain-on-failure',
    // Generous action timeouts so every click is visible
    actionTimeout: 30_000,
  },
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1600, height: 900 },
        launchOptions: { slowMo: 400 },
      },
    },
    {
      // Extra-slow for recording — every click is very deliberate
      name: 'chromium-slow',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1600, height: 900 },
        launchOptions: { slowMo: 900 },
      },
    },
  ],
  // Do NOT start the app — run `dotnet run` in a separate terminal first
  webServer: undefined,
});
