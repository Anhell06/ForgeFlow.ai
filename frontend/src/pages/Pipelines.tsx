import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { api, type Template, type Workflow } from "../api";
import { toastError } from "../components/Toast";
import { useI18n } from "../i18n";

export default function Pipelines() {
  const nav = useNavigate();
  const { t, ago, template } = useI18n();
  const [workflows, setWorkflows] = useState<Workflow[] | null>(null);
  const [templates, setTemplates] = useState<Template[]>([]);

  const load = () => {
    api.workflows().then(setWorkflows).catch(toastError);
    api.templates().then(setTemplates).catch(toastError);
  };
  useEffect(load, []);

  const create = async () => {
    try { nav(`/p/${(await api.createWorkflow(t("pipelines.newName"))).id}`); } catch (e) { toastError(e); }
  };
  const fromTemplate = async (tpl: Template) => {
    try {
      // The new pipeline gets the template's name in the current language.
      const local = template(tpl);
      nav(`/p/${(await api.instantiate(tpl.id, local.name, local.description)).id}`);
    } catch (e) { toastError(e); }
  };
  const remove = async (w: Workflow) => {
    if (!confirm(t("pipelines.confirmDelete", { name: w.name }))) return;
    try { await api.deleteWorkflow(w.id); load(); } catch (e) { toastError(e); }
  };
  const duplicate = async (w: Workflow) => {
    try { await api.duplicateWorkflow(w.id); load(); } catch (e) { toastError(e); }
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>{t("pipelines.title")}</h1>
          <div className="sub">{t("pipelines.sub")}</div>
        </div>
        <button className="primary" onClick={create}>{t("pipelines.new")}</button>
      </div>

      <div className="section">
        <h2>{t("pipelines.yours")}</h2>
        {workflows === null ? <div className="sub">{t("common.loading")}</div> : workflows.length === 0 ? (
          <div className="empty">{t("pipelines.empty")}</div>
        ) : (
          <table className="table card" style={{ padding: 0 }}>
            <thead><tr><th>{t("common.name")}</th><th>{t("pipelines.colInputs")}</th><th>{t("pipelines.colNodes")}</th><th>{t("pipelines.colUpdated")}</th><th /></tr></thead>
            <tbody>
              {workflows.map((w) => (
                <tr key={w.id} className="clickable" onClick={() => nav(`/p/${w.id}`)}>
                  <td><b>{w.name}</b>{w.description && <div className="help" style={{ maxWidth: 560 }}>{w.description}</div>}</td>
                  <td>{w.inputs.map((i) => <span key={i.name} className="tag" style={{ marginRight: 4 }}>{i.name}</span>)}</td>
                  <td>{w.nodeCount}</td>
                  <td className="sub">{ago(w.updatedAt)}</td>
                  <td style={{ textAlign: "right" }} onClick={(e) => e.stopPropagation()}>
                    <button className="small ghost" onClick={() => duplicate(w)}>{t("pipelines.duplicate")}</button>
                    <button className="small ghost danger" onClick={() => remove(w)}>{t("common.delete")}</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="section">
        <h2>{t("pipelines.templates")}</h2>
        <div className="grid">
          {templates.map(template).map((tpl) => (
            <div key={tpl.id} className="tile" onClick={() => fromTemplate(tpl)}>
              <b>{tpl.name}</b>
              <div className="desc">{tpl.description}</div>
              <div className="row">
                <div className="tags">{tpl.tags.map((tag) => <span key={tag} className="tag">{tag}</span>)}</div>
                <span className="spacer" />
                <span className="help">{t("pipelines.nodes", { n: tpl.nodeCount })}</span>
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
