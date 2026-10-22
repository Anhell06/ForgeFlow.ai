import "@google/model-viewer";
import { isAsset, type Asset } from "../api";
import { copy } from "./ui";
import { toast } from "./Toast";
import { useI18n } from "../i18n";

declare module "react" {
  // eslint-disable-next-line @typescript-eslint/no-namespace
  namespace JSX {
    interface IntrinsicElements {
      "model-viewer": DetailedHTMLProps<HTMLAttributes<HTMLElement>, HTMLElement> & {
        src?: string; "camera-controls"?: boolean; "auto-rotate"?: boolean; autoplay?: boolean; "shadow-intensity"?: string;
        exposure?: string; "environment-image"?: string; poster?: string; ar?: boolean;
      };
    }
  }
}

const viewable3d = (a: Asset) => a.kind === "model" && /\.(glb|gltf)$/i.test(a.name);

/** Relative URL so previews work through the dev proxy and behind other hosts. */
export const fileUrl = (a: Asset) => {
  try { return new URL(a.url).pathname; } catch { return a.url; }
};

export function AssetView({ asset, compact }: { asset: Asset; compact?: boolean }) {
  const { t } = useI18n();
  const url = fileUrl(asset);
  let body;
  if (asset.kind === "image") body = <img src={url} alt={asset.name} loading="lazy" />;
  else if (asset.kind === "video") body = <video src={url} controls loop muted autoPlay playsInline />;
  else if (asset.kind === "audio") body = <audio src={url} controls style={{ width: "100%" }} />;
  else if (viewable3d(asset)) body = <model-viewer src={url} camera-controls auto-rotate autoplay shadow-intensity="1" exposure="1.1" />;
  else body = <div style={{ padding: 24, textAlign: "center", color: "var(--muted)" }}>{t("assets.file", { kind: asset.kind })}</div>;
  return (
    <div className="preview">
      {body}
      {!compact && (
        <div className="preview-meta">
          <span className="grow" title={asset.path} style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{asset.name}</span>
          <span>{(asset.size / 1024).toFixed(0)} KB</span>
          <button className="small ghost" title={t("assets.copyPathHint")} onClick={() => { copy(asset.path); toast(t("common.pathCopied")); }}>{t("assets.path")}</button>
          <a className="btn small" href={url} download={asset.name}>↓</a>
        </div>
      )}
    </div>
  );
}

export function AssetThumb({ asset }: { asset: Asset }) {
  const url = fileUrl(asset);
  if (asset.kind === "image") return <img src={url} alt="" loading="lazy" />;
  if (asset.kind === "video") return <video src={url} muted loop playsInline onMouseEnter={(e) => void e.currentTarget.play()} onMouseLeave={(e) => e.currentTarget.pause()} />;
  if (viewable3d(asset)) return <model-viewer src={url} auto-rotate />;
  return <span>{{ model: "⬢", audio: "♪", text: "¶", json: "{}" }[asset.kind] ?? "▤"}</span>;
}

/** Renders any node output value: assets, lists of assets, text or JSON. */
export function ValueView({ value }: { value: unknown }) {
  if (value === null || value === undefined) return <span className="sub">—</span>;
  if (isAsset(value)) return <AssetView asset={value} />;
  if (Array.isArray(value) && value.length > 0 && value.every(isAsset)) {
    return (
      <div className="asset-grid" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(120px, 1fr))" }}>
        {(value as Asset[]).map((a) => (
          <a key={a.$asset} href={fileUrl(a)} target="_blank" rel="noreferrer" className="asset-card" title={a.name}>
            <div className="asset-thumb" style={{ height: 110 }}><AssetThumb asset={a} /></div>
          </a>
        ))}
      </div>
    );
  }
  if (Array.isArray(value) && value.some((v) => Array.isArray(v) || isAsset(v))) {
    return <div className="row wrap" style={{ alignItems: "flex-start" }}>{value.map((v, i) => <div key={i} style={{ width: "100%" }}><ValueView value={v} /></div>)}</div>;
  }
  if (typeof value === "string") return <pre>{value}</pre>;
  return <pre>{JSON.stringify(value, null, 2)}</pre>;
}
