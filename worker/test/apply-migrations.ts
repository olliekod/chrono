import { applyD1Migrations } from "cloudflare:test";
import { env } from "cloudflare:workers";

// Each test file gets fresh storage; give it the real schema.
await applyD1Migrations(env.DB, env.TEST_MIGRATIONS);
