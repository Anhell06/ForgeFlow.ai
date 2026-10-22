import { useEffect, useState } from "react";

type Toast = { id: number; text: string; error?: boolean };
let push: ((t: Omit<Toast, "id">) => void) | null = null;

export function toast(text: string) { push?.({ text }); }
export function toastError(e: unknown) { push?.({ text: e instanceof Error ? e.message : String(e), error: true }); }

export function ToastHost() {
  const [items, setItems] = useState<Toast[]>([]);
  useEffect(() => {
    let n = 0;
    push = (t) => {
      const id = ++n;
      setItems((x) => [...x, { ...t, id }]);
      setTimeout(() => setItems((x) => x.filter((i) => i.id !== id)), t.error ? 7000 : 3000);
    };
    return () => { push = null; };
  }, []);
  return (
    <>
      {items.map((t, i) => (
        <div key={t.id} className={"toast" + (t.error ? " err" : "")} style={{ bottom: 18 + i * 56 }}>{t.text}</div>
      ))}
    </>
  );
}
