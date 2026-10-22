import { useState } from "react";
import { Handle, NodeResizer, Position, type Node, type NodeProps } from "@xyflow/react";
import { isAsset, kindColor, type Asset, type NodeDef, type NodeSpec, type NodeState } from "../api";
import { CanvasPreview, fileUrl, ValueView } from "./Preview";
import { Modal } from "./ui";
import { useI18n, type MsgKey } from "../i18n";

export type ForgeNodeData = { def: NodeDef; spec?: NodeSpec; state?: NodeState };
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
  const { def, state } = data;
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
      {(preview || state?.message || state?.error || thumb || status === "running") && (
        <div className="fnode-foot">
          {preview && !state && <div style={{ overflow: "hidden", textOverflow: "ellipsis", display: "-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient: "vertical" }}>{preview}</div>}
          {state?.error && <div style={{ color: "var(--err)" }}>{state.error.slice(0, 140)}</div>}
          {!state?.error && state?.message && <div>{state.message}{state.items ? ` · ${state.itemsDone ?? 0}/${state.items}` : ""}</div>}
          {status === "running" && <div className="progress"><div style={{ width: `${Math.round((state?.progress ?? 0.05) * 100)}%` }} /></div>}
          {thumb && thumb.kind === "image" && <img className="fnode-thumb" src={fileUrl(thumb)} alt="" />}
          {thumb && thumb.kind === "video" && <video className="fnode-thumb" src={fileUrl(thumb)} muted autoPlay loop playsInline />}
          {thumb && thumb.kind === "model" && <div style={{ marginTop: 6 }}>⬢ {thumb.name}</div>}
        </div>
      )}
    </div>
  );
}
