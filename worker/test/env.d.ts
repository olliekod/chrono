declare namespace Cloudflare {
  interface Env {
    // Test-only binding set in vitest.config.ts
    TEST_MIGRATIONS: Parameters<typeof import("cloudflare:test").applyD1Migrations>[1];
  }
}
