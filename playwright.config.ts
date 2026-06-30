import { defineConfig, devices } from '@playwright/test';

/**
 * Config de smoke tests E2E de Metrocar Nortur Blazor.
 *
 * Apunta a la app corriendo en local (dotnet run → http://localhost:5287).
 * NO levanta la app sola por defecto: arrancá la app primero (dotnet run) y
 * después corré `npm test`. Si querés que Playwright la levante sola, descomentá
 * el bloque `webServer` de abajo.
 *
 * Credenciales: NO se hardcodean. Se leen de variables de entorno
 * NORTUR_USER / NORTUR_PASS (ver tests/.env.example). En PowerShell:
 *   $env:NORTUR_USER="alejandra"; $env:NORTUR_PASS="..."; npm test
 */
export default defineConfig({
  testDir: './tests',
  // Un fallo no debería tardar una eternidad: las pantallas deben levantar rápido.
  timeout: 30_000,
  expect: { timeout: 10_000 },
  // Smoke test: corre en serie para no abrir N circuitos SignalR a la vez contra
  // el SQL Server 2012 (que es sensible a la concurrencia, ver CLAUDE.md).
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.NORTUR_BASEURL ?? 'http://localhost:5287',
    // Blazor Server usa certificados self-signed en https; en http no hace falta,
    // pero lo dejamos tolerante por si se apunta a la URL https.
    ignoreHTTPSErrors: true,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    actionTimeout: 10_000,
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],

  // ── Opcional: que Playwright levante la app sola ──
  // Descomentá si querés que `npm test` arranque dotnet run automáticamente.
  // webServer: {
  //   command: 'dotnet run --project MetroCarSysBlazor/MetroCarSysBlazor.csproj',
  //   url: 'http://localhost:5287',
  //   reuseExistingServer: true,
  //   timeout: 120_000,
  // },
});
