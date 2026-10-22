import type { McpServer, ParamSpec, Provider } from "../api";
import { Field } from "./ui";
import { useI18n } from "../i18n";

interface Props {
  spec: ParamSpec;
  value: unknown;
  onChange: (v: unknown) => void;
  providers: Provider[];
  mcpServers: McpServer[];
}

const asText = (v: unknown) => (v === undefined || v === null ? "" : typeof v === "string" ? v : JSON.stringify(v, null, 2));

/** One editor field generated from a node's ParamSpec. */
export function ParamField({ spec, value, onChange, providers, mcpServers }: Props) {
  const { t } = useI18n();
  const label = spec.label ?? spec.name;
  const v = value === undefined ? spec.default : value;

  switch (spec.kind) {
    case "bool":
      return (
        <label className="row" style={{ marginBottom: 12 }}>
          <input type="checkbox" checked={v === true || v === "true"} onChange={(e) => onChange(e.target.checked)} />
          <span>{label}</span>
        </label>
      );
    case "number":
      return (
        <Field label={label} help={spec.help}>
          <input type="number" value={asText(v)} onChange={(e) => onChange(e.target.value === "" ? undefined : Number(e.target.value))} />
        </Field>
      );
    case "select":
      return (
        <Field label={label} help={spec.help}>
          <select value={asText(v)} onChange={(e) => onChange(e.target.value)}>
            {(spec.options ?? []).map((o) => <option key={o} value={o}>{o}</option>)}
          </select>
        </Field>
      );
    case "provider": {
      const allowed = providers.filter((p) => !spec.providerTypes?.length || spec.providerTypes.includes(p.type));
      const current = asText(v);
      return (
        <Field label={label} help={spec.help}>
          <select value={current} onChange={(e) => onChange(e.target.value || undefined)}>
            <option value="">{t("param.auto")}</option>
            {allowed.map((p) => (
              <option key={p.id} value={p.name}>{p.name} · {p.type}{p.keyAvailable || p.type === "comfyui" || p.type === "http" ? "" : ` · ${t("param.noKey")}`}</option>
            ))}
            {current && !allowed.some((p) => p.name === current || p.id === current) && <option value={current}>{current} ({t("param.missing")})</option>}
          </select>
        </Field>
      );
    }
    case "mcpServers": {
      const selected = Array.isArray(v) ? (v as string[]) : [];
      return (
        <Field label={label} help={spec.help}>
          <div className="card" style={{ padding: 8 }}>
            {mcpServers.length === 0 && <div className="help">{t("param.noMcp")}</div>}
            {mcpServers.map((s) => (
              <label key={s.id} className="row" style={{ padding: "3px 0" }}>
                <input type="checkbox" checked={selected.includes(s.name) || selected.includes(s.id)}
                  onChange={(e) => onChange(e.target.checked ? [...selected, s.name] : selected.filter((x) => x !== s.name && x !== s.id))} />
                <span>{s.name}</span>
                <span className="help">{s.transport}{s.enabled ? "" : ` · ${t("common.disabled")}`}</span>
              </label>
            ))}
          </div>
        </Field>
      );
    }
    case "text":
      return (
        <Field label={label} help={spec.help}>
          <textarea rows={5} value={asText(v)} onChange={(e) => onChange(e.target.value)} />
        </Field>
      );
    case "json":
    case "code":
      return (
        <Field label={label} help={spec.help}>
          <textarea className="code" rows={spec.kind === "code" ? 10 : 4} spellCheck={false} value={asText(v)} onChange={(e) => onChange(e.target.value)} />
        </Field>
      );
    default:
      return (
        <Field label={label} help={spec.help}>
          <input value={asText(v)} onChange={(e) => onChange(e.target.value)} />
        </Field>
      );
  }
}
