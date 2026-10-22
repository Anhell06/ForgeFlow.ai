import { useEffect, useState } from "react";
import { api, type McpServer, type Provider, type ProviderType } from "../api";
import { copy, Field, Modal } from "../components/ui";
import { toast, toastError } from "../components/Toast";
import { LanguageSelect, useI18n } from "../i18n";

export default function Settings() {
  const { t } = useI18n();
  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>{t("settings.title")}</h1>
          <div className="sub">{t("settings.sub")}</div>
        </div>
        <div className="row"><span className="sub">{t("common.language")}</span><LanguageSelect compact /></div>
      </div>
      <Providers />
      <McpServers />
      <CustomNodes />
      <Integration />
    </div>
  );
}

// ---------------- providers ----------------

function Providers() {
  const { t } = useI18n();
  const [list, setList] = useState<Provider[]>([]);
  const [types, setTypes] = useState<ProviderType[]>([]);
  const [edit, setEdit] = useState<Partial<Provider> | null>(null);
  const load = () => api.providers().then(setList).catch(toastError);
  useEffect(() => { void load(); api.providerTypes().then(setTypes).catch(toastError); }, []);

  const save = async () => {
    if (!edit) return;
    try {
      if (edit.id) await api.saveProvider(edit as Provider);
      else await api.createProvider(edit);
      setEdit(null);
      void load();
    } catch (e) { toastError(e); }
  };
  const remove = async (p: Provider) => {
    if (!confirm(t("settings.confirmRemoveProvider", { name: p.name }))) return;
    try { await api.deleteProvider(p.id); void load(); } catch (e) { toastError(e); }
  };
  const typeInfo = types.find((x) => x.type === edit?.type);

  return (
    <div className="section">
      <div className="row" style={{ marginBottom: 10 }}>
        <h2 className="grow" style={{ margin: 0 }}>{t("settings.providers")}</h2>
        <button onClick={() => setEdit({ type: "openai", name: "", extra: {} })}>{t("settings.addProvider")}</button>
      </div>
      <table className="table card" style={{ padding: 0 }}>
        <thead><tr><th>{t("common.name")}</th><th>{t("settings.colType")}</th><th>{t("settings.colEndpoint")}</th><th>{t("settings.colModel")}</th><th>{t("settings.colKey")}</th><th /></tr></thead>
        <tbody>
          {list.map((p) => (
            <tr key={p.id} className="clickable" onClick={() => setEdit({ ...p })}>
              <td><b>{p.name}</b></td>
              <td><span className="tag">{p.type}</span></td>
              <td className="mono help">{p.baseUrl ?? types.find((x) => x.type === p.type)?.defaultBaseUrl ?? t("settings.default")}</td>
              <td className="mono help">{p.defaultModel}</td>
              <td>{p.keyAvailable ? <span className="badge s-succeeded">{t("settings.keyReady")}</span> : p.type === "comfyui" || p.type === "http"
                ? <span className="badge s-pending">{t("settings.keyNotNeeded")}</span> : <span className="badge s-skipped">{p.apiKey ?? t("settings.keyMissing")}</span>}</td>
              <td style={{ textAlign: "right" }} onClick={(e) => e.stopPropagation()}><button className="small ghost danger" onClick={() => remove(p)}>{t("common.remove")}</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="help" style={{ marginTop: 8 }}>{t("settings.providersHelp")}</p>

      {edit && (
        <Modal title={edit.id ? t("settings.editTitle", { name: edit.name ?? "" }) : t("settings.addProviderTitle")} onClose={() => setEdit(null)}>
          <Field label={t("settings.colType")} help={typeInfo?.description}>
            <select value={edit.type} onChange={(e) => setEdit({ ...edit, type: e.target.value })}>
              {types.map((x) => <option key={x.type} value={x.type}>{x.title}</option>)}
            </select>
          </Field>
          <Field label={t("common.name")} help={t("settings.nameHelp")}>
            <input value={edit.name ?? ""} onChange={(e) => setEdit({ ...edit, name: e.target.value })} />
          </Field>
          <Field label={t("settings.baseUrl")} help={typeInfo?.defaultBaseUrl ? t("settings.defaultIs", { url: typeInfo.defaultBaseUrl }) : undefined}>
            <input value={edit.baseUrl ?? ""} placeholder={typeInfo?.defaultBaseUrl} onChange={(e) => setEdit({ ...edit, baseUrl: e.target.value || undefined })} />
          </Field>
          <Field label={t("settings.apiKey")} help={t("settings.apiKeyHelp", { hint: typeInfo?.keyHelp ?? "" })}>
            <input value={edit.apiKey ?? ""} onChange={(e) => setEdit({ ...edit, apiKey: e.target.value })} />
          </Field>
          <Field label={t("settings.colModel")}>
            <input value={edit.defaultModel ?? ""} onChange={(e) => setEdit({ ...edit, defaultModel: e.target.value })} />
          </Field>
          <Field label={t("settings.extra")} help={edit.type === "anthropic" ? t("settings.extraAnthropic") : undefined}>
            <textarea className="code" rows={3} defaultValue={JSON.stringify(edit.extra ?? {}, null, 2)}
              onChange={(e) => { try { setEdit({ ...edit, extra: JSON.parse(e.target.value || "{}") }); } catch { /* keep typing */ } }} />
          </Field>
          <div className="row"><span className="spacer" /><button onClick={() => setEdit(null)}>{t("common.cancel")}</button><button className="primary" onClick={save}>{t("common.save")}</button></div>
        </Modal>
      )}
    </div>
  );
}

// ---------------- MCP servers ----------------

function McpServers() {
  const { t } = useI18n();
  const [list, setList] = useState<McpServer[]>([]);
  const [edit, setEdit] = useState<(Partial<McpServer> & { argsText?: string; envText?: string }) | null>(null);
  const [test, setTest] = useState<{ name: string; result: Awaited<ReturnType<typeof api.testMcp>> | null } | null>(null);
  const load = () => api.mcpServers().then(setList).catch(toastError);
  useEffect(() => { void load(); }, []);

  const open = (s?: McpServer) => setEdit(s
    ? { ...s, argsText: s.args.join("\n"), envText: JSON.stringify(s.env, null, 2) }
    : { transport: "stdio", name: "", command: "", argsText: "", envText: "{}", enabled: true });

  const save = async () => {
    if (!edit) return;
    try {
      const body = {
        ...edit,
        args: (edit.argsText ?? "").split("\n").map((s) => s.trim()).filter(Boolean),
        env: JSON.parse(edit.envText || "{}"),
      };
      if (edit.id) await api.saveMcp(body as McpServer); else await api.createMcp(body);
      setEdit(null);
      void load();
    } catch (e) { toastError(e); }
  };
  const runTest = async (s: McpServer) => {
    setTest({ name: s.name, result: null });
    try { setTest({ name: s.name, result: await api.testMcp(s.id) }); } catch (e) { toastError(e); setTest(null); }
  };

  return (
    <div className="section">
      <div className="row" style={{ marginBottom: 10 }}>
        <h2 className="grow" style={{ margin: 0 }}>{t("settings.mcp")}</h2>
        <button onClick={() => open()}>{t("settings.addMcp")}</button>
      </div>
      <table className="table card" style={{ padding: 0 }}>
        <thead><tr><th>{t("common.name")}</th><th>{t("settings.colTransport")}</th><th>{t("settings.colCommand")}</th><th /></tr></thead>
        <tbody>
          {list.map((s) => (
            <tr key={s.id} className="clickable" onClick={() => open(s)}>
              <td><b>{s.name}</b>{!s.enabled && <span className="tag" style={{ marginLeft: 6 }}>{t("common.disabled")}</span>}</td>
              <td><span className="tag">{s.transport}</span></td>
              <td className="mono help">{s.transport === "http" ? s.url : `${s.command} ${s.args.join(" ")}`}</td>
              <td style={{ textAlign: "right" }} onClick={(e) => e.stopPropagation()}>
                <button className="small" onClick={() => runTest(s)}>{t("settings.test")}</button>
                <button className="small ghost danger" onClick={() => api.deleteMcp(s.id).then(load).catch(toastError)}>{t("common.remove")}</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="help" style={{ marginTop: 8 }}>{t("settings.mcpHelp")}</p>

      {edit && (
        <Modal title={edit.id ? t("settings.editTitle", { name: edit.name ?? "" }) : t("settings.addMcpTitle")} onClose={() => setEdit(null)}>
          <Field label={t("common.name")}><input value={edit.name ?? ""} onChange={(e) => setEdit({ ...edit, name: e.target.value })} /></Field>
          <Field label={t("settings.transport")}>
            <select value={edit.transport} onChange={(e) => setEdit({ ...edit, transport: e.target.value as "stdio" | "http" })}>
              <option value="stdio">{t("settings.stdio")}</option>
              <option value="http">{t("settings.http")}</option>
            </select>
          </Field>
          {edit.transport === "http" ? (
            <Field label="URL"><input value={edit.url ?? ""} onChange={(e) => setEdit({ ...edit, url: e.target.value })} /></Field>
          ) : (
            <>
              <Field label={t("settings.command")} help={t("settings.commandHelp")}><input value={edit.command ?? ""} onChange={(e) => setEdit({ ...edit, command: e.target.value })} /></Field>
              <Field label={t("settings.args")}><textarea rows={3} value={edit.argsText} onChange={(e) => setEdit({ ...edit, argsText: e.target.value })} /></Field>
              <Field label={t("settings.cwd")}><input value={edit.cwd ?? ""} onChange={(e) => setEdit({ ...edit, cwd: e.target.value })} /></Field>
              <Field label={t("settings.env")} help={t("settings.envHelp")}>
                <textarea className="code" rows={3} value={edit.envText} onChange={(e) => setEdit({ ...edit, envText: e.target.value })} />
              </Field>
            </>
          )}
          <label className="row" style={{ marginBottom: 12 }}>
            <input type="checkbox" checked={edit.enabled ?? true} onChange={(e) => setEdit({ ...edit, enabled: e.target.checked })} /> {t("common.enabled")}
          </label>
          <div className="row"><span className="spacer" /><button onClick={() => setEdit(null)}>{t("common.cancel")}</button><button className="primary" onClick={save}>{t("common.save")}</button></div>
        </Modal>
      )}

      {test && (
        <Modal title={t("settings.testTitle", { name: test.name })} onClose={() => setTest(null)}>
          {!test.result ? <div className="sub">{t("settings.connecting")}</div> : test.result.ok ? (
            <>
              <p><span className="badge s-succeeded">{t("settings.connected")}</span> {t("settings.tools", { n: test.result.tools?.length ?? 0 })}</p>
              <div className="logs">{test.result.tools?.map((x) => `${x.name} — ${x.description}`).join("\n")}</div>
            </>
          ) : (
            <>
              <pre style={{ color: "var(--err)" }}>{test.result.error}</pre>
              {test.result.logs.length > 0 && <div className="logs" style={{ marginTop: 10 }}>{test.result.logs.join("\n")}</div>}
            </>
          )}
        </Modal>
      )}
    </div>
  );
}

// ---------------- custom nodes ----------------

const customExample = {
  type: "custom.my-sdxl",
  title: "My SDXL server",
  category: "Custom",
  description: "Text-to-image on my own GPU server.",
  inputs: [{ name: "prompt", kind: "text" }, { name: "image", kind: "image" }],
  params: [{ name: "steps", kind: "number", label: "Steps", default: 30 }],
  http: {
    method: "POST",
    url: "http://localhost:8000/generate",
    bodyMode: "json",
    body: "{\n  \"prompt\": \"{{inputs.prompt | escape}}\",\n  \"steps\": {{params.steps}}\n}",
    responseMode: "json",
    pollUrl: "http://localhost:8000/jobs/{{response.id}}",
    doneWhen: "status=done",
    failWhen: "status=error",
    resultPath: "files",
  },
};

function CustomNodes() {
  const { t } = useI18n();
  const [list, setList] = useState<Record<string, unknown>[]>([]);
  const [text, setText] = useState<string | null>(null);
  const load = () => api.customNodes().then(setList).catch(toastError);
  useEffect(() => { void load(); }, []);

  const save = async () => {
    try { await api.saveCustomNode(JSON.parse(text ?? "{}")); setText(null); void load(); toast(t("settings.nodeSaved")); }
    catch (e) { toastError(e); }
  };

  return (
    <div className="section">
      <div className="row" style={{ marginBottom: 10 }}>
        <h2 className="grow" style={{ margin: 0 }}>{t("settings.custom")}</h2>
        <button onClick={() => setText(JSON.stringify(customExample, null, 2))}>{t("settings.newNode")}</button>
      </div>
      {list.length === 0 ? (
        <div className="empty">{t("settings.customEmpty")}</div>
      ) : (
        <div className="grid">
          {list.map((n) => (
            <div key={String(n.type)} className="tile" onClick={() => setText(JSON.stringify(n, null, 2))}>
              <b>{String(n.title ?? n.type)}</b>
              <div className="mono help">{String(n.type)}</div>
              <div className="desc">{String(n.description ?? "")}</div>
              <div className="row"><span className="spacer" />
                <button className="small ghost danger" onClick={(e) => { e.stopPropagation(); void api.deleteCustomNode(String(n.type)).then(load).catch(toastError); }}>{t("common.delete")}</button>
              </div>
            </div>
          ))}
        </div>
      )}
      {text !== null && (
        <Modal title={t("settings.customTitle")} onClose={() => setText(null)} wide>
          <p className="help">{t("settings.customHelp")}</p>
          <textarea className="code" rows={24} value={text} onChange={(e) => setText(e.target.value)} spellCheck={false} />
          <div className="row" style={{ marginTop: 10 }}><span className="spacer" /><button onClick={() => setText(null)}>{t("common.cancel")}</button><button className="primary" onClick={save}>{t("settings.saveNode")}</button></div>
        </Modal>
      )}
    </div>
  );
}

// ---------------- integration ----------------

function Integration() {
  const { t } = useI18n();
  const origin = window.location.origin.replace(":5173", ":5180");
  const [key, setKey] = useState(() => { try { return localStorage.getItem("forgeflow.apiKey") ?? ""; } catch { return ""; } });
  const snippets = [
    { title: t("settings.snipMcp"), code: `claude mcp add --transport http forgeflow ${origin}/mcp` },
    { title: t("settings.snipRun"), code: `curl -X POST ${origin}/api/workflows/<pipeline-id>/run -H "Content-Type: application/json" -d "{\\"inputs\\":{\\"prompt\\":\\"a knight\\"},\\"wait\\":true}"` },
    { title: t("settings.snipNode"), code: `curl -X POST ${origin}/api/nodes/model.generate3d/run -H "Content-Type: application/json" -d "{\\"inputs\\":{\\"image\\":{\\"$asset\\":\\"ast_...\\"}},\\"params\\":{\\"provider\\":\\"Tripo\\"},\\"wait\\":true}"` },
  ];
  return (
    <div className="section">
      <h2>{t("settings.integration")}</h2>
      <div className="grid" style={{ gridTemplateColumns: "1fr" }}>
        {snippets.map((s) => (
          <div key={s.title} className="card">
            <div className="row" style={{ marginBottom: 8 }}><b className="grow">{s.title}</b><button className="small" onClick={() => { copy(s.code); toast(t("common.copied")); }}>{t("common.copy")}</button></div>
            <pre>{s.code}</pre>
          </div>
        ))}
        <div className="card">
          <b>{t("settings.browserKey")}</b>
          <p className="help">{t("settings.browserKeyHelp")}</p>
          <div className="row">
            <input type="password" value={key} onChange={(e) => setKey(e.target.value)} />
            <button onClick={() => { try { localStorage.setItem("forgeflow.apiKey", key); } catch { /* storage blocked */ } toast(t("common.saved")); }}>{t("common.save")}</button>
          </div>
        </div>
      </div>
      <p className="help" style={{ marginTop: 8 }}>{t("settings.openapi")} <a href="/openapi/v1.json" target="_blank" rel="noreferrer">/openapi/v1.json</a></p>
    </div>
  );
}
