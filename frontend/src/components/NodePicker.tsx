import { useEffect, useMemo, useRef, useState } from "react";
import { compatible, kindColor, type NodeSpec, type PortSpec } from "../api";
import { useI18n } from "../i18n";

/** What the user is dragging: an output (we look for inputs) or an input (we look for outputs). */
export interface PickerOrigin {
  from: "source" | "target";
  port: PortSpec;
}

export interface PickerChoice {
  spec: NodeSpec;
  port: string;
}

interface Candidate extends PickerChoice {
  title: string;
  category: string;
  portLabel: string;
  kind: string;
  exact: boolean;
}

/** Node types with a port that can connect to `origin`, best matches first. */
export function candidatesFor(specs: NodeSpec[], origin: PickerOrigin): Omit<Candidate, "title" | "category" | "portLabel">[] {
  const out: Omit<Candidate, "title" | "category" | "portLabel">[] = [];
  for (const spec of specs) {
    const ports = origin.from === "source"
      ? spec.inputs.filter((p) => compatible(origin.port.kind, p.kind))
      : spec.outputs.filter((p) => compatible(p.kind, origin.port.kind));
    // One entry per node: the best matching port. Same kind beats "any"; then a list port pairs with a list port
    // (images[] ↔ images[]) and a single value with a single value (image ↔ image).
    const score = (p: PortSpec) => (p.kind === origin.port.kind ? 2 : 0) + (p.list === origin.port.list ? 1 : 0);
    const best = [...ports].sort((a, b) => score(b) - score(a))[0];
    if (best) out.push({ spec, port: best.name, kind: best.kind, exact: best.kind === origin.port.kind });
  }
  return out;
}

export function NodePicker({ x, y, specs, origin, onPick, onClose }: {
  x: number; y: number; specs: NodeSpec[]; origin: PickerOrigin;
  onPick: (c: PickerChoice) => void; onClose: () => void;
}) {
  const { t, spec: localize } = useI18n();
  const [q, setQ] = useState("");
  const [active, setActive] = useState(0);
  const boxRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  const items = useMemo(() => {
    const f = q.trim().toLowerCase();
    return candidatesFor(specs, origin)
      .map((c): Candidate => {
        const l = localize(c.spec);
        const p = (origin.from === "source" ? l.inputs : l.outputs).find((x) => x.name === c.port);
        return { ...c, title: l.title, category: l.category, portLabel: p?.label ?? c.port };
      })
      .filter((c) => !f || c.title.toLowerCase().includes(f) || c.spec.type.includes(f) || c.category.toLowerCase().includes(f) || c.portLabel.toLowerCase().includes(f))
      .sort((a, b) => Number(b.exact) - Number(a.exact) || a.category.localeCompare(b.category) || a.title.localeCompare(b.title));
  }, [specs, origin, q, localize]);

  useEffect(() => setActive(0), [q]);
  useEffect(() => {
    listRef.current?.querySelector<HTMLElement>(`[data-i="${active}"]`)?.scrollIntoView({ block: "nearest" });
  }, [active]);

  // Close on any press outside the popup.
  useEffect(() => {
    const onDown = (e: PointerEvent) => { if (!boxRef.current?.contains(e.target as Node)) onClose(); };
    window.addEventListener("pointerdown", onDown, true);
    return () => window.removeEventListener("pointerdown", onDown, true);
  }, [onClose]);

  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === "ArrowDown") { e.preventDefault(); setActive((i) => Math.min(items.length - 1, i + 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setActive((i) => Math.max(0, i - 1)); }
    else if (e.key === "Enter") { e.preventDefault(); if (items[active]) onPick(items[active]); }
    else if (e.key === "Escape") { e.preventDefault(); onClose(); }
  };

  const portName = origin.port.label ?? origin.port.name;
  return (
    <div ref={boxRef} className="picker nodrag nowheel" style={{ left: x, top: y }} onKeyDown={onKey}>
      <div className="picker-head">
        <span className="dot" style={{ background: kindColor[origin.port.kind] ?? "#999", borderRadius: origin.port.list ? 2 : "50%" }} />
        <span className="grow">{t(origin.from === "source" ? "picker.forOutput" : "picker.forInput", { port: portName })}</span>
      </div>
      <input autoFocus placeholder={t("picker.search")} value={q} onChange={(e) => setQ(e.target.value)} />
      <div className="picker-list" ref={listRef}>
        {items.length === 0 && <div className="help" style={{ padding: 10 }}>{t("picker.empty")}</div>}
        {items.map((c, i) => (
          <div key={c.spec.type} data-i={i} className={"picker-item" + (i === active ? " on" : "")}
            onMouseEnter={() => setActive(i)} onClick={() => onPick(c)}>
            <span className="dot" style={{ background: c.spec.color ?? "#64748b" }} />
            <span className="grow">{c.title}<span className="help"> · {c.category}</span></span>
            <span className="picker-port" style={{ color: kindColor[c.kind] ?? "#aaa" }}>{c.portLabel}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
