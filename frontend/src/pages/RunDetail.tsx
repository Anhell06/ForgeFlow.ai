import { useEffect, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { api, formatDuration, subscribe, type Run } from "../api";
import { ValueView } from "../components/Preview";
import { Status } from "../components/ui";
import { toast, toastError } from "../components/Toast";
import { useI18n } from "../i18n";

export default function RunDetail() {
  const { id = "" } = useParams();
  const nav = useNavigate();
  const { t } = useI18n();
  const [run, setRun] = useState<Run | null>(null);
  const [open, setOpen] = useState<string | null>(null);

  useEffect(() => {
    const load = () => api.run(id).then(setRun).catch(toastError);
    void load();
    let timer: number | undefined;
    const off = subscribe(id, () => { clearTimeout(timer); timer = window.setTimeout(load, 250); });
    return () => { off(); clearTimeout(timer); };
  }, [id]);

  if (!run) return <div className="page sub">{t("common.loading")}</div>;
  const nodes = run.graph?.nodes ?? [];
  const active = run.status === "running" || run.status === "queued";

  const rerun = async (fromNodeId?: string) => {
    try { const r = await api.rerun(run.id, fromNodeId); toast(t("run.started")); nav(`/runs/${r.id}`); } catch (e) { toastError(e); }
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <div className="row"><h1>{run.name}</h1><Status status={run.status} /></div>
          <div className="sub mono">{run.id} · {formatDuration(run.startedAt, run.finishedAt)}
            {run.parentRunId && <> · {t("run.rerunOf")} <Link to={`/runs/${run.parentRunId}`}>{run.parentRunId}</Link></>}</div>
        </div>
        <div className="row">
          {active && <button onClick={() => api.cancelRun(run.id).catch(toastError)}>{t("editor.stop")}</button>}
          {!active && <button onClick={() => rerun()}>{t("run.again")}</button>}
          {run.workflowId && <button className="primary" onClick={() => nav(`/p/${run.workflowId}`)}>{t("run.openEditor")}</button>}
        </div>
      </div>

      {run.error && <pre style={{ color: "var(--err)", marginBottom: 20 }}>{run.error}</pre>}

      {Object.keys(run.outputs ?? {}).length > 0 && (
        <div className="section">
          <h2>{t("editor.results")}</h2>
          <div className="grid" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(340px, 1fr))" }}>
            {Object.entries(run.outputs).map(([k, v]) => (
              <div key={k} className="card"><h3>{k}</h3><ValueView value={v} /></div>
            ))}
          </div>
        </div>
      )}

      <div className="section">
        <h2>{t("run.inputs")}</h2>
        <pre>{JSON.stringify(run.inputs, null, 2)}</pre>
      </div>

      <div className="section">
        <h2>{t("run.nodes")}</h2>
        <table className="table card" style={{ padding: 0 }}>
          <thead><tr><th>{t("run.colNode")}</th><th>{t("runs.colStatus")}</th><th>{t("run.colTime")}</th><th>{t("run.colMessage")}</th><th /></tr></thead>
          <tbody>
            {nodes.map((n) => {
              const st = run.nodes?.[n.id];
              return [
                <tr key={n.id} className="clickable" onClick={() => setOpen(open === n.id ? null : n.id)}>
                  <td><b>{n.title ?? n.id}</b><div className="help mono">{n.type}</div></td>
                  <td><Status status={st?.status ?? "pending"} />{st?.cached && <span className="tag" style={{ marginLeft: 6 }}>{t("status.cached")}</span>}</td>
                  <td className="sub">{formatDuration(st?.startedAt, st?.finishedAt)}</td>
                  <td style={{ color: st?.error ? "var(--err)" : undefined, maxWidth: 420, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                    {st?.error ?? st?.message}{st?.items ? ` · ${t("run.items", { done: st.itemsDone ?? 0, total: st.items })}` : ""}
                  </td>
                  <td style={{ textAlign: "right" }} onClick={(e) => e.stopPropagation()}>
                    {!active && <button className="small ghost" onClick={() => rerun(n.id)}>{t("run.fromHere")}</button>}
                  </td>
                </tr>,
                open === n.id && (
                  <tr key={n.id + "-x"}>
                    <td colSpan={5} style={{ background: "var(--bg)" }}>
                      <div className="grid" style={{ gridTemplateColumns: "1fr 1fr" }}>
                        <div>
                          <h3>{t("run.outputs")}</h3>
                          {Object.entries(st?.outputs ?? {}).filter(([, v]) => v !== null).map(([k, v]) => (
                            <div key={k} style={{ marginBottom: 10 }}><div className="help">{k}</div><ValueView value={v} /></div>
                          ))}
                        </div>
                        <div>
                          <h3>{t("editor.tabLogs")}</h3>
                          <div className="logs">{st?.logs?.join("\n") || "—"}</div>
                        </div>
                      </div>
                    </td>
                  </tr>
                ),
              ];
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
