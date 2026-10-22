import { useRef, useState } from "react";

/**
 * Undo/redo stacks of whole-editor snapshots. `record` is called *before* a change with the current state;
 * repeated records with the same key inside `windowMs` merge into one step (typing, nudging a value).
 */
export function useHistory<T>(limit = 50) {
  const undoStack = useRef<T[]>([]);
  const redoStack = useRef<T[]>([]);
  const last = useRef<{ key: string; at: number } | null>(null);
  const [, rerender] = useState(0);

  const record = (snapshot: T, key?: string, windowMs = 1000) => {
    const now = Date.now();
    if (key && last.current?.key === key && now - last.current.at < windowMs) {
      last.current.at = now;
      return;
    }
    last.current = key ? { key, at: now } : null;
    undoStack.current.push(snapshot);
    if (undoStack.current.length > limit) undoStack.current.shift();
    redoStack.current = [];
    rerender((x) => x + 1);
  };

  /** Returns the snapshot to restore, or null; `current` goes onto the other stack. */
  const step = (from: React.RefObject<T[]>, to: React.RefObject<T[]>, current: T): T | null => {
    const target = from.current.pop();
    if (target === undefined) return null;
    to.current.push(current);
    if (to.current.length > limit) to.current.shift();
    last.current = null;
    rerender((x) => x + 1);
    return target;
  };

  return {
    record,
    undo: (current: T) => step(undoStack, redoStack, current),
    redo: (current: T) => step(redoStack, undoStack, current),
    canUndo: undoStack.current.length > 0,
    canRedo: redoStack.current.length > 0,
  };
}
