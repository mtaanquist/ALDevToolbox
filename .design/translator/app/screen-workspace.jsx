/* ============================================================
   New Workspace screen
   ============================================================ */
const TREE = [
  { d: 0, t: "folder", n: "Workspace/" },
  { d: 1, t: "folder", n: "assets/" },
  { d: 2, t: "file", n: "logo.png" },
  { d: 1, t: "folder", n: "Core/" },
  { d: 2, t: "folder", n: "Source/" },
  { d: 3, t: "folder", n: "Foundation/" },
  { d: 4, t: "file", n: "Hello.al" },
  { d: 2, t: "folder", n: "Translations/" },
  { d: 2, t: "file", n: "app.json" },
  { d: 1, t: "folder", n: "Hotfix/" },
  { d: 2, t: "folder", n: "Source/" },
  { d: 2, t: "folder", n: "Translations/" },
  { d: 2, t: "file", n: "app.json" },
  { d: 1, t: "file", n: ".gitignore" },
  { d: 1, t: "file", n: "workspace.aldt.toml" },
  { d: 1, t: "file", n: "Workspace.code-workspace" },
];

const MODULES = [
  { name: "Continia Banking", ver: "v28", dep: 1 },
  { name: "Continia Document Capture + Output", ver: "v28", dep: 2 },
  { name: "ForNAV Reports", ver: "v28", dep: 1 },
  { name: "Sana Commerce Cloud", ver: "v27", dep: 3 },
  { name: "Tasklet Mobile WMS", ver: "v28", dep: 1 },
];

function NewWorkspace() {
  const [sel, setSel] = useState({});
  const selCount = Object.values(sel).filter(Boolean).length;
  const depCount = MODULES.reduce((a, m, i) => a + (sel[i] ? m.dep : 0), 0);

  return (
    <div className="page">
      <div className="page-inner">
        <div className="ws-grid">
          {/* ---- left: form ---- */}
          <div>
            <div className="page-head">
              <h1>New workspace</h1>
              <div className="sub">Spin up a fresh AL workspace from one of your organisation's workspace templates. Pick the template, tweak the defaults that don't fit your project, and download the ZIP.</div>
            </div>
            <div className="fld">
              <label className="cap-label" style={{ display: "block", marginBottom: 8 }}>Workspace template</label>
              <div className="select-wrap">
                <select className="select" defaultValue="ns">
                  <option value="ns">Workspace with namespaces</option>
                  <option value="basic">Basic workspace</option>
                  <option value="appsource">AppSource app</option>
                </select>
                <I.chevDown/>
              </div>
              <div className="help">Project workspace with namespaces, usable from Business Central 2025 release wave 1. · Default application: <code>28.0.0.0</code></div>
            </div>

            <div className="fld">
              <label className="cap-label" style={{ display: "block", marginBottom: 8 }}>Project</label>
              <input className="input" placeholder="e.g. Acme Customer" />
              <div className="help">Workspace name. Letters, digits and spaces only; must start with a letter.</div>
            </div>

            <div className="fld">
              <input className="input" placeholder="e.g. ACME" />
              <div className="help">A short prefix added to the names of generated extensions (e.g. <code>ACME Core</code>). Different from the AL object-name affix set on the template itself.</div>
            </div>

            <div className="fld">
              <textarea className="textarea" rows="1" defaultValue="Customizations made for {{short_name}}"></textarea>
            </div>
            <div className="fld">
              <textarea className="textarea" rows="2" defaultValue="Extension with customisations made in collaboration with {{short_name}} to make Business Central better support their processes."></textarea>
            </div>

            <div className="fld">
              <label className="cap-label" style={{ display: "block", marginBottom: 8 }}>Tenant ID</label>
              <input className="input mono" placeholder="e.g. 8a4f1c3e-1b2d-4e6f-9c1a-3d5b7e9f2a4c" />
              <div className="help">The customer's Microsoft Entra (Azure AD) tenant ID — the GUID their tenant uses, typically pasted from their Business Central environment so the generated debug configs target the right tenant. Optional; leave blank if you don't have it yet. Available in templates and always-included files as <code>{"{{tenant_id}}"}</code>.</div>
            </div>

            <div className="field-row">
              <div className="fld" style={{ marginBottom: 0 }}>
                <label className="cap-label" style={{ display: "block", marginBottom: 8 }}>Core ID range — from</label>
                <input className="input mono" defaultValue="90000" />
              </div>
              <div className="fld" style={{ marginBottom: 0 }}>
                <label className="cap-label" style={{ display: "block", marginBottom: 8 }}>Core ID range — to</label>
                <input className="input mono" defaultValue="90999" />
              </div>
            </div>

            <div className="fld" style={{ marginTop: 22 }}>
              <label className="cap-label" style={{ display: "block", marginBottom: 8 }}>Application version</label>
              <div className="select-wrap">
                <select className="select" defaultValue="w1">
                  <option value="w1">Business Central 2026 release wave 1</option>
                  <option value="w2">Business Central 2025 release wave 2</option>
                </select>
                <I.chevDown/>
              </div>
              <div className="help">application <code>28.0.0.0</code> · runtime <code>17.0</code></div>
            </div>

            {/* modules */}
            <div style={{ marginTop: 28 }}>
              <div className="mod-head">
                <span className="cap-label">Modules</span>
                <span className="selcount"><b>{selCount}</b> selected · <b>{depCount}</b> dependencies pulled in</span>
              </div>
              <div className="mod-list">
                {MODULES.map((m, i) => (
                  <div key={i} className={"mod" + (sel[i] ? " on" : "")} onClick={() => setSel((s) => ({ ...s, [i]: !s[i] }))}>
                    <span className="cbx"><I.check/></span>
                    <span>
                      <span className="mname">{m.name}</span>
                      <span className="mver">{m.ver}</span>
                    </span>
                    <span className="mdep"><I.link/>+ {m.dep} {m.dep === 1 ? "dependency" : "dependencies"}</span>
                  </div>
                ))}
              </div>
            </div>
          </div>

          {/* ---- right: sticky preview ---- */}
          <div className="ws-rail">
            <div className="card">
              <div className="card-head"><span className="cap-label">Preview</span><span className="status-pill"><span className="sd"/>Live</span></div>
              <div className="tree">
                {TREE.map((n, i) => (
                  <div className={"tnode " + n.t} key={i} style={{ paddingLeft: n.d * 16 }}>
                    {n.t === "folder" ? <I.folder/> : <I.file/>}
                    <span className="tname">{n.n}</span>
                  </div>
                ))}
              </div>
            </div>
            <div className="stat-row">
              <div className="stat accent"><span className="sn">2</span><span className="sl">Extensions</span></div>
              <div className="stat"><span className="sn">{depCount}</span><span className="sl">Dependencies</span></div>
            </div>
          </div>
        </div>
      </div>

      {/* sticky action bar */}
      <div className="actionbar">
        <div className="actionbar-inner">
          <div className="ab-info">Generating <b>2 extensions</b> from <b>Workspace with namespaces</b> · BC 2026 wave 1</div>
          <div className="ab-actions">
            <button className="btn lg">Save as template</button>
            <button className="btn primary lg"><I.download/>Download ZIP</button>
          </div>
        </div>
      </div>
    </div>
  );
}

window.NewWorkspace = NewWorkspace;
