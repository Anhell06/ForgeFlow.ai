// Typed client for the ForgeFlow backend.

export type Kind = "text" | "image" | "video" | "model" | "audio" | "file" | "json" | "any";

export interface PortSpec { name: string; kind: Kind; list: boolean; optional: boolean; label?: string; help?: string }
export interface ParamSpec {
  name: string; kind: string; label?: string; default?: unknown; options?: string[]; providerTypes?: string[]; help?: string;
}
export interface NodeSpec {
  type: string; title: string; category: string; description: string;
  inputs: PortSpec[]; outputs: PortSpec[]; params: ParamSpec[]; color?: string; custom: boolean;
}

export interface NodeDef {
  id: string; type: string; title?: string; params: Record<string, unknown>;
  position?: { x: number; y: number }; width?: number; height?: number; retries?: number; mapConcurrency?: number; disabled?: boolean;
}
export interface EdgeDef { id: string; source: string; sourcePort: string; target: string; targetPort: string }
export interface Graph { nodes: NodeDef[]; edges: EdgeDef[] }

export interface WorkflowInput { name: string; type: "text" | "file"; title?: string; default?: unknown }
export interface Workflow {
  id: string; name: string; description: string; templateId?: string; createdAt: string; updatedAt: string;
  nodeCount: number; inputs: WorkflowInput[]; graph?: Graph;
}
export interface Template { id: string; name: string; description: string; tags: string[]; nodeCount: number }

export type NodeStatus = "pending" | "running" | "succeeded" | "failed" | "skipped" | "canceled";
export interface NodeState {
  status: NodeStatus; startedAt?: string; finishedAt?: string; outputs: Record<string, unknown>; error?: string;
  progress?: number; message?: string; logs: string[]; items?: number; itemsDone?: number; cached?: boolean;
}
export type RunStatus = "queued" | "running" | "succeeded" | "failed" | "canceled";
export interface Run {
  id: string; workflowId?: string; name: string; status: RunStatus; error?: string; parentRunId?: string;
  createdAt: string; startedAt?: string; finishedAt?: string;
  inputs: Record<string, unknown>; outputs: Record<string, unknown>;
  nodes?: Record<string, NodeState>; graph?: Graph;
}

export interface Asset { $asset: string; kind: string; name: string; mime: string; url: string; path: string; size: number }
export const isAsset = (v: unknown): v is Asset => !!v && typeof v === "object" && "$asset" in (v as object);

export interface Provider {
  id: string; name: string; type: string; baseUrl?: string; defaultModel?: string; apiKey?: string | null;
  keyAvailable: boolean; extra: Record<string, unknown>;
}
export interface ProviderType { type: string; title: string; defaultBaseUrl?: string; keyHelp: string; description: string }
export interface McpServer {
  id: string; name: string; transport: "stdio" | "http"; command?: string; args: string[]; env: Record<string, string>;
  cwd?: string; url?: string; headers: Record<string, string>; enabled: boolean;
}

export interface RunEvent {
  type: "run.status" | "node.status" | "node.log"; runId: string; ts: string;
  status?: string; nodeId?: string; progress?: number; message?: string; error?: string;
  items?: number; itemsDone?: number; outputs?: Record<string, unknown>; line?: string; name?: string;
}

const apiKey = () => localStorageGet("forgeflow.apiKey");

function localStorageGet(key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  const key = apiKey();
  if (key) headers["X-Api-Key"] = key;
  let payload: BodyInit | undefined;
  if (body instanceof FormData) payload = body;
  else if (body !== undefined) { headers["Content-Type"] = "application/json"; payload = JSON.stringify(body); }
  const res = await fetch(url, { method, headers, body: payload });
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`;
    try { const j = await res.json(); if (j?.error) msg = j.error; } catch { /* not json */ }
    throw new Error(msg);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export const api = {
  nodes: () => request<NodeSpec[]>("GET", "/api/nodes"),
  templates: () => request<Template[]>("GET", "/api/templates"),
  instantiate: (id: string, name?: string, description?: string) =>
    request<Workflow>("POST", `/api/templates/${id}/instantiate`, { name, description }),

  workflows: () => request<Workflow[]>("GET", "/api/workflows"),
  workflow: (id: string) => request<Workflow>("GET", `/api/workflows/${id}`),
  createWorkflow: (name: string, graph?: Graph) => request<Workflow>("POST", "/api/workflows", { name, graph: graph ?? { nodes: [], edges: [] } }),
  saveWorkflow: (id: string, w: { name?: string; description?: string; graph?: Graph }) => request<Workflow>("PUT", `/api/workflows/${id}`, w),
  deleteWorkflow: (id: string) => request<void>("DELETE", `/api/workflows/${id}`),
  duplicateWorkflow: (id: string) => request<Workflow>("POST", `/api/workflows/${id}/duplicate`),
  runWorkflow: (id: string, inputs: Record<string, unknown>) => request<Run>("POST", `/api/workflows/${id}/run`, { inputs }),

  runs: (workflowId?: string, limit = 50) => request<Run[]>("GET", `/api/runs?limit=${limit}${workflowId ? `&workflowId=${workflowId}` : ""}`),
  run: (id: string) => request<Run>("GET", `/api/runs/${id}`),
  cancelRun: (id: string) => request<void>("POST", `/api/runs/${id}/cancel`),
  /** With `graph`, the edited pipeline is used and only changed nodes (and what follows them) re-execute. */
  rerun: (id: string, fromNodeId?: string, graph?: Graph) => request<Run>("POST", `/api/runs/${id}/rerun`, { fromNodeId, graph }),
  deleteRun: (id: string) => request<void>("DELETE", `/api/runs/${id}`),

  assets: (kind?: string, q?: string) => request<Asset[]>("GET", `/api/assets?limit=300${kind ? `&kind=${kind}` : ""}${q ? `&q=${encodeURIComponent(q)}` : ""}`),
  asset: (id: string) => request<Asset>("GET", `/api/assets/${id}`),
  upload: (files: FileList | File[]) => {
    const fd = new FormData();
    for (const f of Array.from(files)) fd.append("files", f);
    return request<Asset[]>("POST", "/api/assets", fd);
  },
  importAsset: (url: string) => request<Asset>("POST", "/api/assets/import", { url }),
  deleteAsset: (id: string) => request<void>("DELETE", `/api/assets/${id}`),

  providerTypes: () => request<ProviderType[]>("GET", "/api/provider-types"),
  providers: () => request<Provider[]>("GET", "/api/providers"),
  createProvider: (p: Partial<Provider>) => request<Provider>("POST", "/api/providers", { ...p, extra: JSON.stringify(p.extra ?? {}) }),
  saveProvider: (p: Partial<Provider> & { id: string }) => request<Provider>("PUT", `/api/providers/${p.id}`, { ...p, extra: JSON.stringify(p.extra ?? {}) }),
  deleteProvider: (id: string) => request<void>("DELETE", `/api/providers/${id}`),

  mcpServers: () => request<McpServer[]>("GET", "/api/mcp-servers"),
  createMcp: (s: Partial<McpServer>) => request<McpServer>("POST", "/api/mcp-servers", s),
  saveMcp: (s: Partial<McpServer> & { id: string }) => request<McpServer>("PUT", `/api/mcp-servers/${s.id}`, s),
  deleteMcp: (id: string) => request<void>("DELETE", `/api/mcp-servers/${id}`),
  testMcp: (id: string) => request<{ ok: boolean; error?: string; tools?: { name: string; description: string }[]; logs: string[] }>("POST", `/api/mcp-servers/${id}/test`),

  customNodes: () => request<Record<string, unknown>[]>("GET", "/api/custom-nodes"),
  saveCustomNode: (def: unknown) => request<NodeSpec[]>("PUT", "/api/custom-nodes", def),
  deleteCustomNode: (type: string) => request<void>("DELETE", `/api/custom-nodes/${encodeURIComponent(type)}`),
};

/** Subscribes to run events over SSE. Returns an unsubscribe function. */
export function subscribe(runId: string | "*", onEvent: (e: RunEvent) => void): () => void {
  const key = apiKey();
  const url = runId === "*" ? "/api/events" : `/api/runs/${runId}/events`;
  const es = new EventSource(key ? `${url}?key=${encodeURIComponent(key)}` : url);
  es.onmessage = (m) => {
    try { onEvent(JSON.parse(m.data) as RunEvent); } catch { /* ignore malformed */ }
  };
  return () => es.close();
}

export const kindColor: Record<string, string> = {
  text: "#60a5fa", image: "#f59e0b", video: "#ef4444", model: "#14b8a6", audio: "#ec4899",
  file: "#a3a3a3", json: "#a78bfa", any: "#e5e7eb",
};

/** Whether an output port of kind `from` may connect to an input port of kind `to`. */
export function compatible(from: string, to: string): boolean {
  if (from === to || from === "any" || to === "any") return true;
  if (to === "file") return from !== "text" && from !== "json";
  if (to === "text") return from === "json";
  if (from === "file") return ["image", "video", "model", "audio"].includes(to);
  return false;
}

export function formatDuration(start?: string, end?: string): string {
  if (!start) return "";
  const ms = (end ? new Date(end).getTime() : Date.now()) - new Date(start).getTime();
  if (ms < 1000) return `${ms} ms`;
  const s = Math.round(ms / 1000);
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s`;
}
