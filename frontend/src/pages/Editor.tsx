import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import {
  addEdge, Background, Controls, MiniMap, ReactFlow, ReactFlowProvider, useEdgesState, useNodesInitialized, useNodesState, useReactFlow,
  type Connection, type Edge, type IsValidConnection,
} from "@xyflow/react";
import {
  api, compatible, formatDuration, subscribe, type Graph, type McpServer, type NodeDef, type NodeSpec, type NodeState, type Provider, type Run,
} from "../api";
import { FlowNode, type ForgeNode } from "../components/FlowNode";
import { ParamField } from "../components/ParamField";
import { ValueView } from "../components/Preview";
import { Field, Modal, Status } from "../components/ui";
import { toast, toastError } from "../components/Toast";
import { LanguageSelect, useI18n } from "../i18n";

const nodeTypes = { forge: FlowNode };

export default function Editor() {
  return (
    <ReactFlowProvider>
      <EditorInner />
    </ReactFlowProvider>
  );
}

function newId(type: string) {
  return `${type.split(".").pop()}_${Math.random().toString(36).slice(2, 6)}`;
}

function EditorInner() {
  const { id = "" } = useParams();
  const navigate = useNavigate();
  const flow = useReactFlow();
  const { t, spec: localize } = useI18n();
  const [specs, setSpecs] = useState<NodeSpec[]>([]);
  const [providers, setProviders] = useState<Provider[]>([]);
  const [mcpServers, setMcpServers] = useState<McpServer[]>([]);
  const [name, setName] = useState("");
  const [nodes, setNodes, onNodesChange] = useNodesState<ForgeNode>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);
  const [run, setRun] = useState<Run | null>(null);
  const [states, setStates] = useState<Record<string, NodeState>>({});
  const [showRun, setShowRun] = useState(false);
  const [filter, setFilter] = useState("");
  const [tab, setTab] = useState<"params" | "output" | "logs">("params");
  const unsub = useRef<(() => void) | null>(null);
  const specMap = useMemo(() => Object.fromEntries(specs.map((s) => [s.type, s])), [specs]);

  // ---- load ----
  useEffect(() => {
    Promise.all([api.nodes(), api.workflow(id), api.providers(), api.mcpServers(), api.runs(id, 1)])
      .then(([sp, wf, pr, mcp, runs]) => {
        setSpecs(sp);
        setProviders(pr);
        setMcpServers(mcp);
        setName(wf.name);
        const map = Object.fromEntries(sp.map((s) => [s.type, s]));
        const g = wf.graph ?? { nodes: [], edges: [] };
        setNodes(g.nodes.map((n, i) => ({
          id: n.id, type: "forge", position: n.position ?? { x: 80 + i * 300, y: 120 },
          data: { def: { ...n, params: n.params ?? {} }, spec: map[n.type] },
        })));
        setEdges(g.edges.map((e) => ({ id: e.id, source: e.source, sourceHandle: e.sourcePort, target: e.target, targetHandle: e.targetPort })));
        if (runs[0]) attachRun(runs[0].id);
      })
      .catch(toastError);
    return () => unsub.current?.();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  // Fit once the loaded nodes have been measured (their size is unknown until first render).
  const nodesReady = useNodesInitialized();
  const fitted = useRef(false);
  useEffect(() => {
    if (nodesReady && !fitted.current && nodes.length > 0) {
      fitted.current = true;
      void flow.fitView({ padding: 0.15 });
    }
  }, [nodesReady, nodes.length, flow]);

  // Mirror run state into node data so nodes render status, progress and thumbnails.
  useEffect(() => {
    setNodes((ns) => ns.map((n) => (n.data.state === states[n.id] ? n : { ...n, data: { ...n.data, state: states[n.id] } })));
    setEdges((es) => es.map((e) => {
      const animated = states[e.source]?.status === "succeeded" && states[e.target]?.status === "running";
      return e.animated === animated ? e : { ...e, animated };
    }));
  }, [states, setNodes, setEdges]);

  const attachRun = useCallback((runId: string) => {
    unsub.current?.();
    const refresh = () => api.run(runId).then((r) => { setRun(r); setStates(r.nodes ?? {}); }).catch(toastError);
    void refresh();
    unsub.current = subscribe(runId, (e) => {
      if (e.type === "node.status" && e.nodeId) {
        setStates((s) => {
          const prev = s[e.nodeId!] ?? { status: "pending", outputs: {}, logs: [] };
          return {
            ...s,
            [e.nodeId!]: {
              ...prev, status: e.status as NodeState["status"], progress: e.progress ?? prev.progress, message: e.message ?? undefined,
              error: e.error ?? undefined, items: e.items ?? prev.items, itemsDone: e.itemsDone ?? prev.itemsDone, outputs: e.outputs ?? prev.outputs,
            },
          };
        });
      } else if (e.type === "node.log" && e.nodeId && e.line) {
        setStates((s) => {
          const prev = s[e.nodeId!] ?? { status: "running", outputs: {}, logs: [] };
          return { ...s, [e.nodeId!]: { ...prev, logs: [...prev.logs, e.line!].slice(-300) } };
        });
      } else if (e.type === "run.status") {
        setRun((r) => (r ? { ...r, status: e.status as Run["status"], error: e.error } : r));
        if (e.status === "succeeded" || e.status === "failed" || e.status === "canceled") void refresh();
      }
    });
  }, []);

  // ---- graph editing ----
  const markDirty = () => setDirty(true);
  const toGraph = useCallback((): Graph => ({
    nodes: flow.getNodes().map((n) => ({ ...(n.data as ForgeNode["data"]).def, position: { x: Math.round(n.position.x), y: Math.round(n.position.y) } })),
    edges: flow.getEdges().map((e) => ({ id: e.id, source: e.source, sourcePort: e.sourceHandle ?? "", target: e.target, targetPort: e.targetHandle ?? "" })),
  }), [flow]);

  const isValidConnection: IsValidConnection = useCallback((c) => {
    if (c.source === c.target) return false;
    const src = flow.getNode(c.source) as ForgeNode | undefined;
    const dst = flow.getNode(c.target) as ForgeNode | undefined;
    const out = src?.data.spec?.outputs.find((p) => p.name === c.sourceHandle);
    const inp = dst?.data.spec?.inputs.find((p) => p.name === c.targetHandle);
    return !!out && !!inp && compatible(out.kind, inp.kind);
  }, [flow]);

  const onConnect = useCallback((c: Connection) => {
    const dst = flow.getNode(c.target) as ForgeNode | undefined;
    const port = dst?.data.spec?.inputs.find((p) => p.name === c.targetHandle);
    setEdges((es) => {
      // A single-value input holds one connection; list inputs collect many.
      const kept = port?.list ? es : es.filter((e) => !(e.target === c.target && e.targetHandle === c.targetHandle));
      return addEdge({ ...c, id: `e_${c.source}_${c.sourceHandle}_${c.target}_${c.targetHandle}` }, kept);
    });
    markDirty();
  }, [flow, setEdges]);

  const addNode = (spec: NodeSpec, position?: { x: number; y: number }) => {
    const pos = position ?? flow.screenToFlowPosition({ x: window.innerWidth / 2, y: window.innerHeight / 2 });
    // Node data keeps the original spec (localized at render); the title is set in the current language.
    const def: NodeDef = { id: newId(spec.type), type: spec.type, title: localize(spec).title, params: {} };
    setNodes((ns) => [...ns.map((n) => ({ ...n, selected: false })), { id: def.id, type: "forge", position: pos, data: { def, spec }, selected: true }]);
    setSelectedId(def.id);
    setTab("params");
    markDirty();
  };

  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    const type = e.dataTransfer.getData("application/forgeflow-node");
    if (specMap[type]) addNode(specMap[type], flow.screenToFlowPosition({ x: e.clientX, y: e.clientY }));
  };

  const updateDef = (nodeId: string, patch: (d: NodeDef) => NodeDef) => {
    setNodes((ns) => ns.map((n) => (n.id === nodeId ? { ...n, data: { ...n.data, def: patch(n.data.def) } } : n)));
    markDirty();
  };

  // ---- save & run ----
  const save = useCallback(async () => {
    try {
      await api.saveWorkflow(id, { name, graph: toGraph() });
      setDirty(false);
      return true;
    } catch (e) {
      toastError(e);
      return false;
    }
  }, [id, name, toGraph]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === "s") { e.preventDefault(); void save().then((ok) => ok && toast(t("common.saved"))); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [save, t]);

  const startRun = async (inputs: Record<string, unknown>) => {
    if (!(await save())) return;
    try {
      const r = await api.runWorkflow(id, inputs);
      setStates({});
      setShowRun(false);
      attachRun(r.id);
      toast(t("editor.runStarted"));
    } catch (e) { toastError(e); }
  };

  const rerunFrom = async (nodeId: string) => {
    if (!run) return;
    if (dirty && !confirm(t("editor.confirmRerunDirty"))) return;
    try {
      const r = await api.rerun(run.id, nodeId);
      attachRun(r.id);
      toast(t("editor.rerunning", { id: nodeId }));
    } catch (e) { toastError(e); }
  };

  const selected = nodes.find((n) => n.id === selectedId);
  const selectedSpec = selected?.data.spec && localize(selected.data.spec);
  const selectedState = selectedId ? states[selectedId] : undefined;
  const inputNodes = nodes.filter((n) => n.data.def.type === "input.text" || n.data.def.type === "input.asset");
  const running = run?.status === "running" || run?.status === "queued";

  const categories = useMemo(() => {
    const f = filter.toLowerCase();
    const groups: Record<string, NodeSpec[]> = {};
    for (const s of specs.map(localize))
      if (!f || s.title.toLowerCase().includes(f) || s.type.includes(f) || s.description.toLowerCase().includes(f))
        (groups[s.category] ??= []).push(s);
    return Object.entries(groups);
  }, [specs, filter, localize]);

  return (
    <div className="editor">
      <div className="editor-top">
        <button className="ghost" onClick={() => navigate("/")}>←</button>
        <input className="title" value={name} onChange={(e) => { setName(e.target.value); markDirty(); }} />
        {dirty && <span className="help">{t("editor.unsaved")}</span>}
        <span className="spacer" />
        {run && (
          <span className="row">
            <Status status={run.status} />
            <Link to={`/runs/${run.id}`} className="help">{formatDuration(run.startedAt, run.finishedAt)} · {t("editor.details")}</Link>
          </span>
        )}
        <LanguageSelect compact />
        {running && <button onClick={() => run && api.cancelRun(run.id).catch(toastError)}>{t("editor.stop")}</button>}
        <button onClick={() => void save().then((ok) => ok && toast(t("common.saved")))}>{t("common.save")}</button>
        <button className="primary" onClick={() => (inputNodes.length ? setShowRun(true) : void startRun({}))}>{t("editor.run")}</button>
      </div>

      <div className="palette">
        <input placeholder={t("editor.search")} value={filter} onChange={(e) => setFilter(e.target.value)} />
        {categories.map(([cat, list]) => (
          <div key={cat}>
            <div className="palette-cat">{cat}</div>
            {list.map((s) => (
              <div key={s.type} className="palette-item" draggable title={s.description}
                onDragStart={(e) => e.dataTransfer.setData("application/forgeflow-node", s.type)}
                onDoubleClick={() => addNode(specMap[s.type])}>
                <span className="dot" style={{ background: s.color ?? "#64748b" }} />
                {s.title}
              </div>
            ))}
          </div>
        ))}
        <div className="help" style={{ marginTop: 14 }}>{t("editor.paletteHelp")}</div>
      </div>

      <div className="canvas" onDragOver={(e) => e.preventDefault()} onDrop={onDrop}>
        <ReactFlow<ForgeNode, Edge>
          nodes={nodes}
          edges={edges}
          nodeTypes={nodeTypes}
          onNodesChange={(c) => { onNodesChange(c); if (c.some((x) => x.type === "remove" || (x.type === "position" && !x.dragging))) markDirty(); }}
          onEdgesChange={(c) => { onEdgesChange(c); if (c.some((x) => x.type === "remove")) markDirty(); }}
          onConnect={onConnect}
          isValidConnection={isValidConnection}
          onSelectionChange={({ nodes: sel }) => setSelectedId(sel[0]?.id ?? null)}
          deleteKeyCode={["Delete", "Backspace"]}
          colorMode="dark"
          fitView
          minZoom={0.2}
          proOptions={{ hideAttribution: true }}
        >
          <Background gap={20} size={1} />
          <Controls />
          <MiniMap pannable zoomable nodeColor={(n) => (n as ForgeNode).data.spec?.color ?? "#555"} />
        </ReactFlow>
      </div>

      <div className="inspector">
        {!selected || !selectedSpec ? (
          <div>
            <h3>{t("editor.pipeline")}</h3>
            <p className="help">{t("editor.helpTemplates")} <code>{"{{inputs.port}}"}</code>, <code>{"{{workflow.inputName}}"}</code>,
              {" "}{t("editor.helpFilters")} <code>| escape</code> (JSON), <code>| datauri</code>, <code>| path</code>, <code>| join:", "</code>.</p>
            <p className="help">{t("editor.helpFanout")}</p>
            {run?.error && <><h3 style={{ marginTop: 16 }}>{t("editor.lastError")}</h3><pre style={{ color: "var(--err)" }}>{run.error}</pre></>}
            {run && Object.keys(run.outputs ?? {}).length > 0 && (
              <>
                <h3 style={{ marginTop: 16 }}>{t("editor.results")}</h3>
                {Object.entries(run.outputs).map(([k, v]) => <div key={k} style={{ marginBottom: 12 }}><div className="help">{k}</div><ValueView value={v} /></div>)}
              </>
            )}
          </div>
        ) : (
          <div>
            <div className="row" style={{ marginBottom: 6 }}>
              <span className="dot" style={{ background: selectedSpec.color }} />
              <b className="grow">{selectedSpec.title}</b>
              {selectedState && <Status status={selectedState.cached ? "succeeded" : selectedState.status} label={selectedState.cached ? t("status.cached") : undefined} />}
            </div>
            <p className="help">{selectedSpec.description}</p>
            <div className="tabs">
              {(["params", "output", "logs"] as const).map((k) => (
                <button key={k} className={tab === k ? "on" : ""} onClick={() => setTab(k)}>
                  {k === "params" ? t("editor.tabSettings") : k === "output" ? t("editor.tabOutput")
                    : `${t("editor.tabLogs")}${selectedState?.logs?.length ? ` (${selectedState.logs.length})` : ""}`}
                </button>
              ))}
            </div>

            {tab === "params" && (
              <div>
                <Field label={t("editor.title")}>
                  <input value={selected.data.def.title ?? ""} onChange={(e) => updateDef(selected.id, (d) => ({ ...d, title: e.target.value }))} />
                </Field>
                {selectedSpec.params.map((p) => (
                  <ParamField key={p.name} spec={p} value={selected.data.def.params[p.name]} providers={providers} mcpServers={mcpServers}
                    onChange={(v) => updateDef(selected.id, (d) => ({ ...d, params: { ...d.params, [p.name]: v } }))} />
                ))}
                <details>
                  <summary className="help" style={{ cursor: "pointer", marginBottom: 10 }}>{t("editor.execution")}</summary>
                  <div className="row">
                    <Field label={t("editor.retries")}><input type="number" min={0} value={selected.data.def.retries ?? 0}
                      onChange={(e) => updateDef(selected.id, (d) => ({ ...d, retries: Number(e.target.value) }))} /></Field>
                    <Field label={t("editor.parallel")}><input type="number" min={1} value={selected.data.def.mapConcurrency ?? 2}
                      onChange={(e) => updateDef(selected.id, (d) => ({ ...d, mapConcurrency: Number(e.target.value) }))} /></Field>
                  </div>
                  <label className="row" style={{ marginBottom: 12 }}>
                    <input type="checkbox" checked={!!selected.data.def.disabled} onChange={(e) => updateDef(selected.id, (d) => ({ ...d, disabled: e.target.checked }))} />
                    <span>{t("editor.bypass")}</span>
                  </label>
                </details>
                <div className="help mono">id: {selected.id}</div>
              </div>
            )}

            {tab === "output" && (
              <div>
                {run && !running && (
                  <button style={{ marginBottom: 12 }} onClick={() => rerunFrom(selected.id)} title={t("editor.rerunFromHint")}>
                    {t("editor.rerunFrom")}
                  </button>
                )}
                {selectedState?.error && <pre style={{ color: "var(--err)", marginBottom: 12 }}>{selectedState.error}</pre>}
                {!selectedState || Object.keys(selectedState.outputs ?? {}).length === 0 ? <div className="help">{t("editor.noOutput")}</div> :
                  Object.entries(selectedState.outputs).filter(([, v]) => v !== null && !(Array.isArray(v) && v.length === 0)).map(([k, v]) => (
                    <div key={k} style={{ marginBottom: 14 }}>
                      <div className="help" style={{ marginBottom: 4 }}>{k}</div>
                      <ValueView value={v} />
                    </div>
                  ))}
              </div>
            )}

            {tab === "logs" && <div className="logs">{selectedState?.logs?.join("\n") || t("editor.noLogs")}</div>}
          </div>
        )}
      </div>

      {showRun && <RunDialog inputs={inputNodes.map((n) => n.data.def)} onClose={() => setShowRun(false)} onRun={startRun} />}
    </div>
  );
}

function RunDialog({ inputs, onClose, onRun }: { inputs: NodeDef[]; onClose: () => void; onRun: (v: Record<string, unknown>) => void }) {
  const [values, setValues] = useState<Record<string, unknown>>(() =>
    Object.fromEntries(inputs.map((d) => [String(d.params.name ?? (d.type === "input.text" ? "prompt" : "image")), d.type === "input.text" ? d.params.value ?? "" : d.params.asset ?? ""])));
  const [busy, setBusy] = useState(false);
  const { t } = useI18n();

  const upload = async (name: string, files: FileList | null) => {
    if (!files?.length) return;
    setBusy(true);
    try {
      const [a] = await api.upload(files);
      setValues((v) => ({ ...v, [name]: a.$asset }));
    } catch (e) { toastError(e); } finally { setBusy(false); }
  };

  return (
    <Modal title={t("editor.runDialog")} onClose={onClose}>
      {inputs.map((d) => {
        const name = String(d.params.name ?? (d.type === "input.text" ? "prompt" : "image"));
        const value = String(values[name] ?? "");
        return d.type === "input.text" ? (
          <Field key={d.id} label={`${d.title ?? name} · ${name}`}>
            <textarea rows={3} value={value} onChange={(e) => setValues({ ...values, [name]: e.target.value })} />
          </Field>
        ) : (
          <Field key={d.id} label={`${d.title ?? name} · ${name}`} help={t("editor.assetHelp")}>
            <div className="row">
              <input value={value} onChange={(e) => setValues({ ...values, [name]: e.target.value })} />
              <label className="btn">{t("editor.upload")}<input type="file" hidden onChange={(e) => upload(name, e.target.files)} /></label>
            </div>
          </Field>
        );
      })}
      <div className="row">
        <span className="spacer" />
        <button onClick={onClose}>{t("common.cancel")}</button>
        <button className="primary" disabled={busy} onClick={() => onRun(values)}>{t("editor.run")}</button>
      </div>
    </Modal>
  );
}
