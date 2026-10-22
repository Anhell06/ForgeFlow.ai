import "@google/model-viewer";
import { useEffect, useRef, useState } from "react";
import { isAsset, type Asset } from "../api";
import { copy } from "./ui";
import { toast } from "./Toast";
import { useI18n } from "../i18n";

declare module "react" {
  // eslint-disable-next-line @typescript-eslint/no-namespace
  namespace JSX {
    interface IntrinsicElements {
      "model-viewer": DetailedHTMLProps<HTMLAttributes<HTMLElement>, HTMLElement> & {
        src?: string; class?: string; "camera-controls"?: boolean; "auto-rotate"?: boolean; autoplay?: boolean; "shadow-intensity"?: string;
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

/** Column count that gives the biggest square cells for n items in a w×h box. */
export function bestColumns(n: number, w: number, h: number): number {
  let best = 1, bestSize = 0;
  for (let cols = 1; cols <= n; cols++) {
    const rows = Math.ceil(n / cols);
    const size = Math.min(w / cols, h / rows);
    if (size > bestSize) { bestSize = size; best = cols; }
  }
  return best;
}

/** Image gallery that always fits the whole box: every image is visible without scrolling. */
function FitGrid({ assets }: { assets: Asset[] }) {
  const ref = useRef<HTMLDivElement>(null);
  const [box, setBox] = useState({ w: 1, h: 1 });
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const ro = new ResizeObserver(([e]) => setBox({ w: e.contentRect.width, h: e.contentRect.height }));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const cols = bestColumns(assets.length, box.w, box.h);
  const rows = Math.ceil(assets.length / cols);
  return (
    <div ref={ref} className="cp-grid" style={{ gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`, gridTemplateRows: `repeat(${rows}, minmax(0, 1fr))` }}>
      {/* Index keys: the same asset may legitimately appear twice (two edges from one output). */}
      {assets.map((a, i) => <img key={i} src={fileUrl(a)} alt={a.name} title={a.name} />)}
    </div>
  );
}

/** Flattens nested lists so a gallery can show every item. */
function flatten(v: unknown): unknown[] {
  return Array.isArray(v) ? v.flatMap(flatten) : [v];
}

/**
 * Compact viewer that fills a canvas node: one asset fills the area, several images form a grid,
 * text and JSON scroll. Interactive parts carry nodrag/nowheel so React Flow leaves them alone.
 */
export function CanvasPreview({ value }: { value: unknown }) {
  const items = flatten(value).filter((v) => v !== null && v !== undefined);
  if (items.length === 0) return null;

  const assets = items.filter(isAsset) as Asset[];
  const others = items.filter((v) => !isAsset(v));

  if (assets.length === 1 && others.length === 0) {
    const a = assets[0];
    const url = fileUrl(a);
    if (a.kind === "image") return <img className="cp-fill" src={url} alt={a.name} />;
    if (a.kind === "video") return <video className="cp-fill nodrag" src={url} controls loop muted autoPlay playsInline />;
    if (a.kind === "audio") return <div className="cp-center nodrag"><audio src={url} controls style={{ width: "100%" }} /></div>;
    if (viewable3d(a)) return <model-viewer class="cp-fill nodrag nowheel" src={url} camera-controls auto-rotate autoplay shadow-intensity="1" exposure="1.1" />;
    return <div className="cp-center"><a className="btn nodrag" href={url} download={a.name}>↓ {a.name}</a></div>;
  }

  if (others.length === 0 && assets.every((a) => a.kind === "image")) return <FitGrid assets={assets} />;

  if (assets.length === 0 && items.length === 1 && typeof items[0] !== "object") {
    return <pre className="cp-text nodrag nowheel">{String(items[0])}</pre>;
  }

  // Mixed content: stack everything and scroll.
  return (
    <div className="cp-stack nodrag nowheel">
      {items.map((v, i) => (
        <div key={i}>
          {isAsset(v) ? <AssetView asset={v} compact /> : <pre>{typeof v === "string" ? v : JSON.stringify(v, null, 2)}</pre>}
        </div>
      ))}
    </div>
  );
}

/** Renders any node output value: assets, lists of assets, text or JSON. */
export function ValueView({ value }: { value: unknown }) {
  if (value === null || value === undefined) return <span className="sub">—</span>;
  if (isAsset(value)) return <AssetView asset={value} />;
  if (Array.isArray(value) && value.length > 0 && value.every(isAsset)) {
    return (
      <div className="asset-grid" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(120px, 1fr))" }}>
        {(value as Asset[]).map((a, i) => (
          <a key={i} href={fileUrl(a)} target="_blank" rel="noreferrer" className="asset-card" title={a.name}>
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
