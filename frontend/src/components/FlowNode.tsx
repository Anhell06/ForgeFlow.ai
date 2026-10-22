import { createContext, useContext, useEffect, useState, type DragEvent } from "react";
import { Handle, NodeResizer, Position, type Node, type NodeProps } from "@xyflow/react";
import { api, isAsset, kindColor, type Asset, type NodeDef, type NodeSpec, type NodeState } from "../api";
import { toastError } from "./Toast";
import { CanvasPreview, fileUrl, ValueView } from "./Preview";
import { Modal } from "./ui";
import { useI18n, type MsgKey } from "../i18n";

/** Set on a preview fed by a single-value port (e.g. image) when the sibling list port (images) holds more. */
export type PreviewHint = { source: string; from: string; to: string; shown: number; total: number };
export type ForgeNodeData = { def: NodeDef; spec?: NodeSpec; state?: NodeState; hint?: PreviewHint };

/** Editor actions nodes can trigger without threading callbacks through node data. */
export const EditorActions = createContext<{
  showAll?: (previewId: string, hint: PreviewHint) => void;
  /** Changes one param of a node (undoable, typing merges into one step). */
  setParam?: (nodeId: string, name: string, value: unknown) => void;
}>({});
export type ForgeNode = Node<ForgeNodeData, "forge">;

function firstAsset(outputs: Record<string, unknown> | undefined): Asset | undefined {
  if (!outputs) return undefined;
  for (const v of Object.values(outputs)) {
    if (isAsset(v)) return v;
    if (Array.isArray(v)) {
      const flat = v.flat(2).find(isAsset);
      if (flat) return flat as Asset;
    }
  }
  return undefined;
}

/** Preview node: a resizable viewer that fills with the incoming result. */
function PreviewNode({ data, selected }: NodeProps<ForgeNode>) {
  const { t, spec: localize } = useI18n();
  const [full, setFull] = useState(false);
  const actions = useContext(EditorActions);
  const { def, state, hint } = data;
  const spec = data.spec && localize(data.spec);
  const value = state?.outputs?.value;
  const has = value !== null && value !== undefined && !(Array.isArray(value) && value.length === 0);
  const single = isAsset(value) ? value : undefined;
  const status = state?.status;

  return (
    <>
      <NodeResizer isVisible={!!selected} minWidth={220} minHeight={180} color="#7c5cff" />
      <div className={`fnode fnode-preview${selected ? " selected" : ""}${status ? ` st-${status}` : ""}`}>
        <div className="fnode-head" style={{ background: `linear-gradient(90deg, ${spec?.color ?? "#10b981"}33, transparent)` }}>
          <span className="dot" style={{ background: spec?.color ?? "#10b981" }} />
          <div className="fnode-title">{def.title || spec?.title}</div>
          {status === "running" && <span className="badge s-running" style={{ padding: "1px 6px", fontSize: 10.5 }}>{t("status.running")}</span>}
          {has && single && <a className="btn small nodrag" href={fileUrl(single)} download={single.name} title={t("preview.download")}>↓</a>}
          {has && <button className="small nodrag" title={t("preview.expand")} onClick={() => setFull(true)}>⤢</button>}
        </div>
        <div className="port" style={{ position: "absolute", top: 46, left: 0, height: 0 }}>
          <Handle type="target" position={Position.Left} id="value" style={{ background: kindColor.any, borderRadius: 2 }} />
        </div>
        <div className="port" style={{ position: "absolute", top: 46, right: 0, height: 0 }}>
          <Handle type="source" position={Position.Right} id="value" style={{ background: kindColor.any }} />
        </div>
        {hint && (
          <div className="cp-hint">
            <span className="grow">{t("preview.partial", { shown: hint.shown, total: hint.total, port: hint.to })}</span>
            {actions.showAll && <button className="small nodrag" onClick={() => actions.showAll!(def.id, hint)}>{t("preview.showAll")}</button>}
          </div>
        )}
        <div className="cp-area">
          {has ? <CanvasPreview value={value} /> : (
            <div className="cp-empty">{state?.error ? <span style={{ color: "var(--err)" }}>{state.error}</span> : t("preview.empty")}</div>
          )}
        </div>
      </div>
      {full && (
        <Modal title={def.title || spec?.title || ""} onClose={() => setFull(false)} wide>
          <ValueView value={value} />
        </Modal>
      )}
    </>
  );
}

export function FlowNode(props: NodeProps<ForgeNode>) {
  if (props.data.def.type === "output.preview") return <PreviewNode {...props} />;
  return <StandardNode {...props} />;
}

function StandardNode({ data, selected }: NodeProps<ForgeNode>) {
  const { t, spec: localize } = useI18n();
  const { def, state } = data;
  const spec = data.spec && localize(data.spec);
  const color = spec?.color ?? "#64748b";
  const status = state?.status;
  const thumb = firstAsset(state?.outputs);
  const inline = def.type === "input.text" || def.type === "input.asset";
  const preview = typeof def.params?.prompt === "string" ? (def.params.prompt as string)
    : typeof def.params?.value === "string" ? (def.params.value as string) : "";

  return (
    <div className={`fnode${selected ? " selected" : ""}${status ? ` st-${status}` : ""}${def.disabled ? " disabled" : ""}`}>
      <div className="fnode-head" style={{ background: `linear-gradient(90deg, ${color}33, transparent)` }}>
        <span className="dot" style={{ background: color }} />
        <div className="grow">
          <div className="fnode-title">{def.title || spec?.title || def.type}</div>
          <div className="fnode-type">{def.type}</div>
        </div>
        {status && status !== "pending" && <span className={`badge s-${status}`} style={{ padding: "1px 6px", fontSize: 10.5 }}>{t((state?.cached ? "status.cached" : `status.${status}`) as MsgKey)}</span>}
      </div>
      <div className="fnode-ports">
        <div className="fnode-col">
          {(spec?.inputs ?? []).map((p) => (
            <div key={p.name} className="port" title={`${p.kind}${p.list ? "[]" : ""}${p.help ? " — " + p.help : ""}`}>
              <Handle type="target" position={Position.Left} id={p.name}
                style={{ background: kindColor[p.kind] ?? "#999", borderRadius: p.list ? 2 : "50%" }} />
              {p.label ?? p.name}{p.list ? " []" : ""}
            </div>
          ))}
        </div>
        <div className="fnode-col">
          {(spec?.outputs ?? []).map((p) => (
            <div key={p.name} className="port out" title={`${p.kind}${p.list ? "[]" : ""}`}>
              {p.label ?? p.name}{p.list ? " []" : ""}
              <Handle type="source" position={Position.Right} id={p.name}
                style={{ background: kindColor[p.kind] ?? "#999", borderRadius: p.list ? 2 : "50%" }} />
            </div>
          ))}
        </div>
      </div>
      {def.type === "input.text" && <InlineText def={def} />}
      {def.type === "input.asset" && <InlineAsset def={def} />}
      {(preview && !inline || state?.message || state?.error || thumb && !inline || status === "running") && (
        <div className="fnode-foot">
          {preview && !inline && !state && <div style={{ overflow: "hidden", textOverflow: "ellipsis", display: "-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient: "vertical" }}>{preview}</div>}
          {state?.error && <div style={{ color: "var(--err)" }}>{state.error.slice(0, 140)}</div>}
          {!state?.error && state?.message && <div>{state.message}{state.items ? ` · ${state.itemsDone ?? 0}/${state.items}` : ""}</div>}
          {status === "running" && <div className="progress"><div style={{ width: `${Math.round((state?.progress ?? 0.05) * 100)}%` }} /></div>}
          {thumb && !inline && thumb.kind === "image" && <img className="fnode-thumb" src={fileUrl(thumb)} alt="" />}
          {thumb && !inline && thumb.kind === "video" && <video className="fnode-thumb" src={fileUrl(thumb)} muted autoPlay loop playsInline />}
          {thumb && !inline && thumb.kind === "model" && <div style={{ marginTop: 6 }}>⬢ {thumb.name}</div>}
        </div>
      )}
    </div>
  );
}

/** The value of a Text input, edited right on the canvas. */
function InlineText({ def }: { def: NodeDef }) {
  const { t } = useI18n();
  const actions = useContext(EditorActions);
  const value = typeof def.params.value === "string" ? def.params.value : "";
  return (
    <div className="fnode-edit">
      <textarea className="nodrag nowheel" rows={4} value={value} placeholder={t("node.textPlaceholder")} spellCheck={false}
        onChange={(e) => actions.setParam?.(def.id, "value", e.target.value)} />
    </div>
  );
}

/** The file of a File input: id/URL/path field, upload button, drop target and a preview of the chosen asset. */
function InlineAsset({ def }: { def: NodeDef }) {
  const { t } = useI18n();
  const actions = useContext(EditorActions);
  const value = typeof def.params.asset === "string" ? def.params.asset : "";
  const [info, setInfo] = useState<Asset | null>(null);
  const [busy, setBusy] = useState(false);
  const [over, setOver] = useState(false);

  useEffect(() => {
    let alive = true;
    if (/^ast_[0-9a-f]+$/i.test(value.trim())) api.asset(value.trim()).then((a) => alive && setInfo(a)).catch(() => alive && setInfo(null));
    else setInfo(null);
    return () => { alive = false; };
  }, [value]);

  const upload = async (files: FileList | null) => {
    if (!files?.length) return;
    setBusy(true);
    try {
      const [a] = await api.upload(files);
      actions.setParam?.(def.id, "asset", a.$asset);
    } catch (e) { toastError(e); } finally { setBusy(false); }
  };
  const hasFiles = (e: DragEvent) => e.dataTransfer.types.includes("Files");

  return (
    <div className={"fnode-edit" + (over ? " over" : "")}
      onDragOver={(e) => { if (hasFiles(e)) { e.preventDefault(); e.stopPropagation(); setOver(true); } }}
      onDragLeave={() => setOver(false)}
      onDrop={(e) => { if (hasFiles(e)) { e.preventDefault(); e.stopPropagation(); setOver(false); void upload(e.dataTransfer.files); } }}>
      <div className="row">
        <input className="nodrag" value={value} placeholder={t("node.assetPlaceholder")} spellCheck={false}
          onChange={(e) => actions.setParam?.(def.id, "asset", e.target.value)} />
        <label className="btn small nodrag" title={t("editor.upload")}>
          {busy ? "…" : "⤒"}
          <input type="file" hidden onChange={(e) => void upload(e.target.files)} />
        </label>
      </div>
      {info && info.kind === "image" && <img className="fnode-thumb" src={fileUrl(info)} alt={info.name} />}
      {info && info.kind === "video" && <video className="fnode-thumb" src={fileUrl(info)} muted autoPlay loop playsInline />}
      {info && info.kind !== "image" && info.kind !== "video" && <div className="help" style={{ marginTop: 6 }}>{info.kind === "model" ? "⬢" : "▤"} {info.name}</div>}
      {!value && <div className="help" style={{ marginTop: 6 }}>{t("node.dropHint")}</div>}
    </div>
  );
}
