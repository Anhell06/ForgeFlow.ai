import { useEffect, useState } from "react";
import { api, type Asset } from "../api";
import { AssetThumb, AssetView } from "../components/Preview";
import { copy, Modal } from "../components/ui";
import { toast, toastError } from "../components/Toast";
import { useI18n, type MsgKey } from "../i18n";

const kinds = ["", "image", "video", "model", "audio", "json", "file"];

export default function Assets() {
  const { t } = useI18n();
  const [assets, setAssets] = useState<Asset[] | null>(null);
  const [kind, setKind] = useState("");
  const [q, setQ] = useState("");
  const [over, setOver] = useState(false);
  const [open, setOpen] = useState<Asset | null>(null);
  const [url, setUrl] = useState("");

  const load = () => api.assets(kind || undefined, q || undefined).then(setAssets).catch(toastError);
  useEffect(() => { void load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [kind, q]);

  const upload = async (files: FileList | null) => {
    if (!files?.length) return;
    try { await api.upload(files); toast(t("assets.uploaded", { n: files.length })); void load(); } catch (e) { toastError(e); }
  };
  const importUrl = async () => {
    if (!url) return;
    try { await api.importAsset(url); setUrl(""); void load(); } catch (e) { toastError(e); }
  };
  const remove = async (a: Asset) => {
    if (!confirm(t("assets.confirmDelete", { name: a.name }))) return;
    try { await api.deleteAsset(a.$asset); setOpen(null); void load(); } catch (e) { toastError(e); }
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>{t("assets.title")}</h1>
          <div className="sub">{t("assets.sub")}</div>
        </div>
        <div className="row">
          <select value={kind} onChange={(e) => setKind(e.target.value)} style={{ width: 150 }}>
            {kinds.map((k) => <option key={k} value={k}>{k ? t(`kind.${k}` as MsgKey) : t("assets.allKinds")}</option>)}
          </select>
          <input placeholder={t("assets.search")} value={q} onChange={(e) => setQ(e.target.value)} style={{ width: 200 }} />
        </div>
      </div>

      <div className={"dropzone" + (over ? " over" : "")} style={{ marginBottom: 18 }}
        onDragOver={(e) => { e.preventDefault(); setOver(true); }} onDragLeave={() => setOver(false)}
        onDrop={(e) => { e.preventDefault(); setOver(false); void upload(e.dataTransfer.files); }}>
        <div className="row wrap" style={{ justifyContent: "center" }}>
          {t("assets.drop")}
          <label className="btn">{t("assets.browse")}<input type="file" multiple hidden onChange={(e) => upload(e.target.files)} /></label>
          <span className="sub">{t("assets.orImport")}</span>
          <input placeholder="https://… or C:\path\file.glb" value={url} onChange={(e) => setUrl(e.target.value)} style={{ width: 280 }} />
          <button onClick={importUrl}>{t("assets.import")}</button>
        </div>
      </div>

      {assets === null ? <div className="sub">{t("common.loading")}</div> : assets.length === 0 ? <div className="empty">{t("assets.empty")}</div> : (
        <div className="asset-grid">
          {assets.map((a) => (
            <div key={a.$asset} className="asset-card" onClick={() => setOpen(a)}>
              <div className="asset-thumb"><AssetThumb asset={a} /></div>
              <div className="asset-name" title={a.name}>{a.name}</div>
            </div>
          ))}
        </div>
      )}

      {open && (
        <Modal title={open.name} onClose={() => setOpen(null)} wide>
          <AssetView asset={open} />
          <div className="row wrap" style={{ marginTop: 12 }}>
            <button onClick={() => { copy(open.$asset); toast(t("assets.idCopied")); }}>{t("assets.copyId")} <span className="mono help">{open.$asset}</span></button>
            <button onClick={() => { copy(open.path); toast(t("common.pathCopied")); }}>{t("assets.copyPath")}</button>
            <span className="spacer" />
            <button className="danger" onClick={() => remove(open)}>{t("common.delete")}</button>
          </div>
        </Modal>
      )}
    </div>
  );
}
