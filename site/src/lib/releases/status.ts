// Shape of /api/status, served by site/server (provance-status).

export interface Package {
  name: string;
  version: string;
  publishedAt: string | null;
  url: string;
}

export interface Release {
  tag: string;
  name: string;
  publishedAt: string | null;
  url: string;
  language: string;
}

export interface Run {
  id: number;
  workflow: string;
  path: string;
  title: string;
  status: string;
  conclusion: string | null;
  branch: string;
  event: string;
  runNumber: number;
  startedAt: string | null;
  updatedAt: string | null;
  url: string;
}

export interface Implementation {
  id: string;
  name: string;
  registry: string;
  packages: Package[];
  release: Release | null;
  pipeline: Run | null;
}

export interface SourceState {
  ok: boolean;
  checkedAt: string | null;
  error: string | null;
}

export interface Snapshot {
  generatedAt: string;
  repository: string;
  active: boolean;
  implementations: Implementation[];
  runs: Run[];
  sources: Record<string, SourceState>;
}

export function runState(run: Run): { label: string; tone: "running" | "good" | "bad" | "neutral" } {
  if (run.status === "queued" || run.status === "waiting" || run.status === "pending" || run.status === "requested") {
    return { label: "Queued", tone: "running" };
  }
  if (run.status !== "completed") return { label: "Running", tone: "running" };
  switch (run.conclusion) {
    case "success":
      return { label: "Succeeded", tone: "good" };
    case "failure":
    case "timed_out":
    case "startup_failure":
      return { label: "Failed", tone: "bad" };
    case "cancelled":
      return { label: "Cancelled", tone: "neutral" };
    default:
      return { label: run.conclusion ?? "Done", tone: "neutral" };
  }
}

export function ago(iso: string | null, now: number): string {
  if (!iso) return "unknown";
  const s = Math.max(0, Math.round((now - Date.parse(iso)) / 1000));
  if (s < 60) return `${s} s ago`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min ago`;
  const h = Math.round(m / 60);
  if (h < 48) return `${h} h ago`;
  return new Date(iso).toISOString().slice(0, 10);
}

export function duration(run: Run, now: number): string | null {
  if (!run.startedAt) return null;
  const end = run.status === "completed" && run.updatedAt ? Date.parse(run.updatedAt) : now;
  const s = Math.max(0, Math.round((end - Date.parse(run.startedAt)) / 1000));
  return s < 60 ? `${s} s` : `${Math.floor(s / 60)} min ${String(s % 60).padStart(2, "0")} s`;
}
