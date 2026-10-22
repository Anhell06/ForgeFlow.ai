import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { api, formatDuration, subscribe, type Run } from "../api";
import { Status } from "../components/ui";
import { toastError } from "../components/Toast";
import { useI18n } from "../i18n";

export default function Runs() {
  const nav = useNavigate();
  const { t, ago } = useI18n();
  const [runs, setRuns] = useState<Run[] | null>(null);

  useEffect(() => {
    const load = () => api.runs(undefined, 200).then(setRuns).catch(toastError);
    void load();
    let timer: number | undefined;
    // Status changes of any run refresh the list (debounced).
    const off = subscribe("*", (e) => {
      if (e.type !== "run.status") return;
      clearTimeout(timer);
      timer = window.setTimeout(load, 300);
    });
    return () => { off(); clearTimeout(timer); };
  }, []);

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>{t("runs.title")}</h1>
          <div className="sub">{t("runs.sub")}</div>
        </div>
      </div>
      {runs === null ? <div className="sub">{t("common.loading")}</div> : runs.length === 0 ? <div className="empty">{t("runs.empty")}</div> : (
        <table className="table card" style={{ padding: 0 }}>
          <thead><tr><th>{t("runs.colStatus")}</th><th>{t("runs.colPipeline")}</th><th>{t("runs.colStarted")}</th><th>{t("runs.colDuration")}</th><th>{t("runs.colError")}</th><th /></tr></thead>
          <tbody>
            {runs.map((r) => (
              <tr key={r.id} className="clickable" onClick={() => nav(`/runs/${r.id}`)}>
                <td><Status status={r.status} /></td>
                <td><b>{r.name}</b>{r.parentRunId && <span className="tag" style={{ marginLeft: 6 }}>{t("runs.rerunTag")}</span>}<div className="help mono">{r.id}</div></td>
                <td className="sub">{ago(r.createdAt)}</td>
                <td className="sub">{formatDuration(r.startedAt, r.finishedAt)}</td>
                <td style={{ color: "var(--err)", maxWidth: 380, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{r.error}</td>
                <td onClick={(e) => e.stopPropagation()} style={{ textAlign: "right" }}>
                  {r.workflowId && <button className="small ghost" onClick={() => nav(`/p/${r.workflowId}`)}>{t("runs.openPipeline")}</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
