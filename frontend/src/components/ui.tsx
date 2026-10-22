import { useEffect, type ReactNode } from "react";
import { useI18n, type MsgKey } from "../i18n";

export function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: ReactNode; wide?: boolean }) {
  const { t } = useI18n();
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <div className="modal-back" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className={"modal" + (wide ? " wide" : "")}>
        <div className="modal-head">
          <h2 style={{ margin: 0 }}>{title}</h2>
          <button className="ghost" onClick={onClose} aria-label={t("common.close")}>✕</button>
        </div>
        {children}
      </div>
    </div>
  );
}

/** Status pill; `label` overrides the text (e.g. "cached") while keeping the status color. */
export function Status({ status, label }: { status: string; label?: string }) {
  const { t } = useI18n();
  return <span className={`badge s-${status}`}>{label ?? t(`status.${status}` as MsgKey)}</span>;
}

export function Field({ label, help, children }: { label: string; help?: string; children: ReactNode }) {
  return (
    <label className="field">
      <span>{label}</span>
      {children}
      {help && <div className="help">{help}</div>}
    </label>
  );
}

export function copy(text: string) {
  void navigator.clipboard?.writeText(text);
}
