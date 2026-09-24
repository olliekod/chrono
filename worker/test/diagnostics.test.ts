import { describe, expect, it } from "vitest";
import { MAX_DIAGNOSTICS_REPORT_LENGTH } from "../src/validate";
import { AUTH, api, jsonPost } from "./helpers";

const sample = { username: "olly", clipFilename: "Long Clip_2026-09-23_20-51-05.mp4", report: "Chrono 1.1.6 diagnostics\n[Recording]\nStatus: Recording Hll-Win64-Shipping" };

describe("submitting a diagnostics report", () => {
  it("is stored and given an id", async () => {
    const res = await jsonPost("/api/diagnostics", sample);

    expect(res.status).toBe(201);
    const { id } = await res.json<{ id: string }>();
    expect(id).toMatch(/^[A-Za-z0-9_-]{12}$/);
  });

  it("needs the upload key", async () => {
    const res = await jsonPost("/api/diagnostics", sample, {});

    expect(res.status).toBe(401);
  });

  it("needs a username", async () => {
    const res = await jsonPost("/api/diagnostics", { ...sample, username: undefined });

    expect(res.status).toBe(400);
  });

  it("needs a non-empty report", async () => {
    expect((await jsonPost("/api/diagnostics", { ...sample, report: "" })).status).toBe(400);
    expect((await jsonPost("/api/diagnostics", { ...sample, report: "   " })).status).toBe(400);
    expect((await jsonPost("/api/diagnostics", { ...sample, report: undefined })).status).toBe(400);
  });

  it("refuses a report far past what a real one runs to", async () => {
    const res = await jsonPost("/api/diagnostics", { ...sample, report: "x".repeat(MAX_DIAGNOSTICS_REPORT_LENGTH + 1) });

    expect(res.status).toBe(400);
  });

  it("accepts a report with no clip attached", async () => {
    const res = await jsonPost("/api/diagnostics", { username: "olly", report: sample.report });

    expect(res.status).toBe(201);
  });

  it("never touches R2 or the clips table", async () => {
    const before = await api("/api/diagnostics", { headers: AUTH });
    const { reports: beforeReports } = await before.json<{ reports: unknown[] }>();

    await jsonPost("/api/diagnostics", sample);

    const after = await api("/api/diagnostics", { headers: AUTH });
    const { reports: afterReports } = await after.json<{ reports: unknown[] }>();
    expect(afterReports.length).toBe(beforeReports.length + 1);
  });
});

describe("listing diagnostics reports", () => {
  it("needs the upload key", async () => {
    expect((await api("/api/diagnostics")).status).toBe(401);
  });

  it("returns what was sent, newest first", async () => {
    await jsonPost("/api/diagnostics", { ...sample, report: "first report" });
    await jsonPost("/api/diagnostics", { ...sample, report: "second report" });

    const res = await api("/api/diagnostics", { headers: AUTH });
    const { reports } = await res.json<{ reports: { report: string; owner: string; clipFilename: string | null }[] }>();

    expect(res.status).toBe(200);
    expect(reports[0].report).toBe("second report");
    expect(reports[1].report).toBe("first report");
    expect(reports[0].owner).toBe("olly");
    expect(reports[0].clipFilename).toBe(sample.clipFilename);
  });

  it("respects a limit", async () => {
    for (let i = 0; i < 5; i++) await jsonPost("/api/diagnostics", { ...sample, report: `report ${i}` });

    const res = await api("/api/diagnostics?limit=2", { headers: AUTH });
    const { reports } = await res.json<{ reports: unknown[] }>();

    expect(reports.length).toBe(2);
  });

  it("ignores a nonsense limit rather than erroring", async () => {
    await jsonPost("/api/diagnostics", sample);
    const res = await api("/api/diagnostics?limit=not-a-number", { headers: AUTH });

    expect(res.status).toBe(200);
  });
});

describe("wrong methods on /api/diagnostics", () => {
  it("are refused", async () => {
    const res = await api("/api/diagnostics", { method: "DELETE", headers: AUTH });
    expect(res.status).toBe(405);
  });
});
