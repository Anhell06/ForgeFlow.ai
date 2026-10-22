import { NavLink, Route, Routes, useLocation } from "react-router-dom";
import Pipelines from "./pages/Pipelines";
import Editor from "./pages/Editor";
import Runs from "./pages/Runs";
import RunDetail from "./pages/RunDetail";
import Assets from "./pages/Assets";
import Settings from "./pages/Settings";
import { ToastHost } from "./components/Toast";
import { LanguageSelect, useI18n, type MsgKey } from "./i18n";

const nav: { to: string; label: MsgKey; icon: string }[] = [
  { to: "/", label: "nav.pipelines", icon: "◇" },
  { to: "/runs", label: "nav.runs", icon: "▷" },
  { to: "/assets", label: "nav.library", icon: "▦" },
  { to: "/settings", label: "nav.settings", icon: "⚙" },
];

export default function App() {
  const loc = useLocation();
  const { t } = useI18n();
  // The editor uses the whole window; everything else sits next to the sidebar.
  if (loc.pathname.startsWith("/p/")) {
    return (
      <>
        <Routes>
          <Route path="/p/:id" element={<Editor />} />
        </Routes>
        <ToastHost />
      </>
    );
  }
  return (
    <div className="app">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">
            <svg width="16" height="16" viewBox="0 0 32 32"><path d="M7 24l9-16 9 16" stroke="white" strokeWidth="4" fill="none" strokeLinejoin="round" /></svg>
          </div>
          ForgeFlow
        </div>
        {nav.map((n) => (
          <NavLink key={n.to} to={n.to} end={n.to === "/"} className={({ isActive }) => "navlink" + (isActive ? " active" : "")}>
            <span style={{ width: 16, textAlign: "center" }}>{n.icon}</span>
            {t(n.label)}
          </NavLink>
        ))}
        <div className="sidebar-foot">
          <div style={{ marginBottom: 10 }}><LanguageSelect /></div>
          {t("nav.tagline")}<br />
          <a href="/openapi/v1.json" target="_blank" rel="noreferrer">OpenAPI</a> · <span className="mono">/mcp</span>
        </div>
      </aside>
      <main className="main">
        <Routes>
          <Route path="/" element={<Pipelines />} />
          <Route path="/runs" element={<Runs />} />
          <Route path="/runs/:id" element={<RunDetail />} />
          <Route path="/assets" element={<Assets />} />
          <Route path="/settings" element={<Settings />} />
        </Routes>
      </main>
      <ToastHost />
    </div>
  );
}
