/* ============================================================
   Cookbook — recipe grid + recipe detail (with AL highlighting)
   ============================================================ */

/* ---------- tiny AL syntax highlighter ---------- */
const AL_KW = new Set([
  "codeunit", "page", "pageextension", "tableextension", "table", "report", "enum",
  "local", "internal", "protected", "procedure", "trigger", "var", "begin", "end",
  "if", "then", "else", "exit", "repeat", "until", "while", "do", "case", "of",
  "layout", "actions", "addlast", "addfirst", "addafter", "addbefore", "modify",
  "extends", "implements", "true", "false", "or", "and", "not", "div", "mod", "in",
]);
const AL_TYPE = new Set([
  "Record", "Boolean", "Integer", "Text", "Code", "Decimal", "BigInteger", "Date",
  "DateTime", "Guid", "Option", "Variant", "RecordRef", "FieldRef",
  "Page", "Codeunit", "ObjectType", "ApplicationArea", "ToolTip", "Caption",
  "Editable", "Locked", "SourceExpr", "Visible",
]);

function alTokens(line) {
  const out = [];
  let i = 0;
  const n = line.length;
  const word = (c) => /[A-Za-z0-9_]/.test(c);
  while (i < n) {
    const c = line[i];
    if (c === "/" && line[i + 1] === "/") { out.push(["com", line.slice(i)]); break; }
    if (c === "'") {
      let j = i + 1;
      while (j < n && line[j] !== "'") j++;
      j = Math.min(j + 1, n);
      out.push(["str", line.slice(i, j)]); i = j; continue;
    }
    if (c === '"') {
      let j = i + 1;
      while (j < n && line[j] !== '"') j++;
      j = Math.min(j + 1, n);
      out.push(["id", line.slice(i, j)]); i = j; continue;
    }
    if (word(c)) {
      let j = i;
      while (j < n && word(line[j])) j++;
      const w = line.slice(i, j);
      let t = "txt";
      if (/^\d/.test(w)) t = "num";
      else if (AL_KW.has(w.toLowerCase())) t = "kw";
      else if (AL_TYPE.has(w)) t = "type";
      out.push([t, w]); i = j; continue;
    }
    // group runs of punctuation/space
    let j = i;
    while (j < n && !word(line[j]) && line[j] !== "'" && line[j] !== '"' && !(line[j] === "/" && line[j + 1] === "/")) j++;
    out.push(["punc", line.slice(i, j)]); i = j;
  }
  return out;
}

function CodeBlock({ file, idx, open, onToggle, onCopy }) {
  const lines = file.code.split("\n");
  return (
    <div className={"codeblock" + (open ? "" : " collapsed")}>
      <div className="cb-bar">
        <button className="cb-name" onClick={onToggle}>
          <I.chevDown size={15} className="chev" />
          <I.fileCode size={15} />
          {file.name}
          <span className="cb-lines">{lines.length} lines</span>
        </button>
        <button className="cb-copy" onClick={() => onCopy(file)}>
          <I.copy size={14} />Copy
        </button>
      </div>
      {open && (
        <div className="cb-body">
          <pre><code>
            {lines.map((ln, li) => (
              <span className="cl" key={li}>
                <span className="cl-n">{li + 1}</span>
                <span className="cl-c">
                  {alTokens(ln).map(([t, v], ti) => (
                    <span key={ti} className={"tok-" + t}>{v}</span>
                  ))}
                  {ln.length === 0 ? "\u200b" : ""}
                </span>
              </span>
            ))}
          </code></pre>
        </div>
      )}
    </div>
  );
}

/* ---------- recipe type badge ---------- */
function RType({ type }) {
  const m = RECIPE_TYPE_META[type];
  return <span className={"rtype " + type}>{I[m.icon]({ size: 13 })}{m.label}</span>;
}

/* ---------- Cookbook grid ---------- */
const CB_FILTERS = ["all", "snippet", "pattern", "module"];

function Cookbook({ onOpenRecipe }) {
  const [filter, setFilter] = useState("all");
  const [q, setQ] = useState("");
  const [dep, setDep] = useState(false);

  const shown = RECIPES.filter((r) => {
    if (filter !== "all" && r.type !== filter) return false;
    if (q) {
      const hay = (r.title + " " + r.desc + " " + r.tags.join(" ")).toLowerCase();
      if (!hay.includes(q.toLowerCase())) return false;
    }
    return true;
  });

  return (
    <div className="page">
      <div className="page-inner">
        <div className="cb-head">
          <div>
            <h1>Cookbook</h1>
            <div className="sub">Reusable AL recipes — from one-file snippets to almost-complete modules. Search by title, description, or keywords; open one to view its files and copy them into your project.</div>
          </div>
          <button className="btn"><I.bulb />Suggest a recipe</button>
        </div>

        <div className="cb-toolbar">
          <div className="cb-search">
            <I.search />
            <input className="input" placeholder="Search the cookbook…" value={q} onChange={(e) => setQ(e.target.value)} />
          </div>
          <div className="cb-pills">
            {CB_FILTERS.map((f) => (
              <button key={f} className={"cb-pill" + (filter === f ? " on" : "")} onClick={() => setFilter(f)}>
                {f === "all" ? "All" : RECIPE_TYPE_META[f].label}
              </button>
            ))}
          </div>
          <label className="cb-toggle">
            <input type="checkbox" checked={dep} onChange={(e) => setDep(e.target.checked)} />
            <span className="cbx-box"><I.check size={12} /></span>
            Include deprecated
          </label>
        </div>

        <div className="cb-grid">
          {shown.map((r) => (
            <button className="recipe-card" key={r.id} onClick={() => onOpenRecipe(r)}>
              <div className="rcd-top">
                <RType type={r.type} />
                <span className="rcd-min"><I.tag size={12} /><span>Min: {r.min}</span></span>
              </div>
              <h3>{r.title}</h3>
              <p className="rcd-desc">{r.desc}</p>
              <div className="rcd-tags">
                {r.tags.slice(0, 6).map((t) => <span className="rcd-tag" key={t}>{t}</span>)}
                {r.tags.length > 6 && <span className="rcd-tag more">+{r.tags.length - 6}</span>}
              </div>
              <div className="rcd-foot">
                <span><I.fileCode size={14} /><span>{r.files} file{r.files === 1 ? "" : "s"}</span></span>
                <span className="rcd-open">View recipe<I.arrowRight size={14} /></span>
              </div>
            </button>
          ))}
        </div>
        {shown.length === 0 && (
          <div className="rel-empty">
            <div className="rel-empty-ico"><I.search size={24} /></div>
            <div className="rel-empty-h">No recipes match</div>
            <div className="rel-empty-p">Try a different keyword or clear the type filter.</div>
          </div>
        )}
      </div>
    </div>
  );
}

/* ---------- Recipe detail ---------- */
function RecipeDetail({ recipe, onBack }) {
  const files = RECIPE_FILES;
  const [openSet, setOpenSet] = useState(() => files.map(() => true));
  const [active, setActive] = useState(0);
  const [toast, setToast] = useState(null);
  const tRef = useRef(null);

  const flash = (msg) => {
    setToast(msg);
    clearTimeout(tRef.current);
    tRef.current = setTimeout(() => setToast(null), 1900);
  };
  const toggle = (i) => setOpenSet((s) => s.map((v, k) => (k === i ? !v : v)));
  const copy = (file) => {
    try { navigator.clipboard && navigator.clipboard.writeText(file.code); } catch (e) {}
    flash("Copied " + file.name);
  };
  const pickFile = (i) => {
    setActive(i);
    setOpenSet((s) => s.map((v, k) => (k === i ? true : v)));
  };

  return (
    <div className="page">
      <div className="page-inner">
        <div className="rd-topbar">
          <button className="btn" onClick={onBack}><I.arrowLeft />Back to cookbook</button>
        </div>

        <div className="rd-grid">
          <div className="rd-main">
            <div className="rd-head">
              <div className="rd-badges">
                <RType type={recipe.type} />
                <span className="rd-min"><I.tag size={13} /><span>Min: {recipe.min} <span className="mono">({recipe.minVer})</span></span></span>
              </div>
              <h1>{recipe.title}</h1>
              <p className="rd-desc">{recipe.desc}</p>
              <div className="rd-tags">
                {recipe.tags.map((t) => <span className="rcd-tag" key={t}>{t}</span>)}
              </div>
            </div>

            <div className="rd-files-h">
              <span className="cap-label">Files</span>
              <span className="rd-files-n">{files.length} file{files.length === 1 ? "" : "s"}</span>
            </div>

            {files.map((f, i) => (
              <CodeBlock key={i} file={f} idx={i} open={openSet[i]} onToggle={() => toggle(i)} onCopy={copy} />
            ))}
          </div>

          {/* sticky rail */}
          <aside className="rd-rail">
            <div className="card">
              <div className="card-head"><span className="cap-label">Files</span></div>
              <div className="rail-files">
                {files.map((f, i) => (
                  <button key={i} className={"rail-file" + (active === i ? " on" : "")} onClick={() => pickFile(i)}>
                    <I.fileCode size={14} />
                    <span className="rf-name">{f.name}</span>
                  </button>
                ))}
              </div>
            </div>
            <div className="rd-meta">
              <div className="rdm-row"><span>Type</span><RType type={recipe.type} /></div>
              <div className="rdm-row"><span>Min. version</span><b className="mono">{recipe.minVer}</b></div>
              <div className="rdm-row"><span>Files</span><b>{files.length}</b></div>
              <button className="btn primary" style={{ width: "100%", justifyContent: "center", marginTop: 4 }} onClick={() => flash("Downloading " + recipe.files + " files…")}><I.download />Download all</button>
            </div>
          </aside>
        </div>
      </div>

      <div className={"toast" + (toast ? " show" : "")}><I.checkCircle />{toast}</div>
    </div>
  );
}

window.Cookbook = Cookbook;
window.RecipeDetail = RecipeDetail;
