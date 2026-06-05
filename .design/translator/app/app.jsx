/* ============================================================
   Root app — exploration strip, theme, header, interactions
   ============================================================ */
const { useEffect, useRef, useMemo } = React;

function applyTheme(mode) {
  const root = document.documentElement;
  if (mode === "system") {
    const dark = window.matchMedia("(prefers-color-scheme: dark)").matches;
    root.setAttribute("data-theme", dark ? "dark" : "light");
  } else {
    root.setAttribute("data-theme", mode);
  }
}

function App() {
  const [units, setUnits] = useState(() => UNITS.map((u) => ({ ...u })));
  const [filter, setFilter] = useState("todo");
  const [kind, setKind] = useState("All kinds");
  const [view, setView] = useState("list");
  const [search, setSearch] = useState("");
  const [theme, setTheme] = useState("light");
  const [fileName, setFileName] = useState(FILE.name);
  const [tgtLang, setTgtLang] = useState(() => LANGS.find((l) => l.code === "da-DK") || LANGS[0]);
  const [editingName, setEditingName] = useState(false);
  const [toast, setToast] = useState(null);
  const toastTimer = useRef(null);

  // initial selection: first unit needing translation
  const [selId, setSelId] = useState(() => {
    const f = UNITS.find((u) => u.state === "untrans" || u.state === "fuzzy");
    return (f || UNITS[0]).id;
  });

  useEffect(() => { applyTheme(theme); }, [theme]);
  useEffect(() => {
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    const h = () => { if (theme === "system") applyTheme("system"); };
    mq.addEventListener("change", h);
    return () => mq.removeEventListener("change", h);
  }, [theme]);

  const allCounts = useMemo(() => counts(units), [units]);

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return units.filter((u) => {
      const fOk = filter === "all"
        ? true
        : filter === "todo"
          ? (u.state === "untrans" || u.state === "fuzzy")
          : (u.state === "trans" || u.state === "final");
      const kOk = kind === "All kinds" || u.kind === kind;
      const sOk = !q || u.src.toLowerCase().includes(q) || (u.tgt || "").toLowerCase().includes(q);
      return fOk && kOk && sOk;
    });
  }, [units, filter, kind, search]);

  const hiddenCount = units.length - filtered.length;

  // keep a valid selection inside the filtered set
  useEffect(() => {
    if (filtered.length === 0) return;
    if (!filtered.some((u) => u.id === selId)) setSelId(filtered[0].id);
  }, [filtered, selId]);

  const sel = units.find((u) => u.id === selId) || null;

  const flash = (msg) => {
    setToast(msg);
    clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToast(null), 1900);
  };

  /* ---- mutations ---- */
  const patch = (id, fields) => setUnits((us) => us.map((u) => (u.id === id ? { ...u, ...fields } : u)));
  const onTarget = (id, text) => patch(id, { tgt: text });
  const onState = (id, st) => patch(id, { state: st });
  const onSelect = (id) => setSelId(id);
  const onFill = (id, text) => { patch(id, { tgt: text, state: "trans" }); flash("Filled from translation memory · marked translated"); };

  const onNav = (dir) => {
    const i = filtered.findIndex((u) => u.id === selId);
    if (i < 0) { if (filtered[0]) setSelId(filtered[0].id); return; }
    const n = Math.min(filtered.length - 1, Math.max(0, i + dir));
    setSelId(filtered[n].id);
  };

  const saveNext = () => {
    if (!sel) return;
    patch(sel.id, { state: sel.tgt.trim() ? "trans" : sel.state });
    // jump to next still-needing-translation unit
    const pool = units.filter((u) => (u.state === "untrans" || u.state === "fuzzy") && u.id !== sel.id);
    if (pool.length) { setSelId(pool[0].id); flash("Saved · jumped to next unit needing translation"); }
    else flash("Saved · nothing left to translate 🎉".replace(" 🎉", ""));
  };

  const preTranslate = () => {
    let n = 0;
    setUnits((us) => us.map((u) => {
      if ((u.state === "untrans" || u.state === "fuzzy") && u.suggs && u.suggs[0] && u.suggs[0].sim >= 99) {
        n++; return { ...u, tgt: u.suggs[0].text, state: "trans" };
      }
      return u;
    }));
    flash(n ? `Pre-filled ${n} exact match${n > 1 ? "es" : ""} from memory` : "No exact memory matches to pre-fill");
  };

  /* ---- keyboard chords (Alt + physical code) ---- */
  useEffect(() => {
    const onKey = (e) => {
      if (!e.altKey) return;
      if (e.code === "Enter") { e.preventDefault(); saveNext(); return; }
      if (e.code === "ArrowUp") { e.preventDefault(); onNav(-1); return; }
      if (e.code === "ArrowDown") { e.preventDefault(); onNav(1); return; }
      const m = /^Digit([1-9])$/.exec(e.code);
      if (m && sel && sel.suggs && sel.suggs[m[1] - 1]) {
        e.preventDefault(); onFill(sel.id, sel.suggs[m[1] - 1].text);
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [sel, filtered, selId, units]);

  const layoutProps = { units: filtered, sel, selId, onSelect, onTarget, onState, onFill, onNav, tgtLang };

  return (
    <>
      {/* ---------- App ---------- */}
      <div className="app">
        <Sidebar />
        <div className="main">
          <Topbar theme={theme} setTheme={setTheme} />
          <div className="content">
            <div className="tr">
              {/* header */}
              <div className="tr-head">
                <div className="row1">
                  <div>
                    <div className="tr-title">
                      <h1><span style={{ color: "var(--blue)", display: "grid", placeItems: "center" }}><I.languages size={24}/></span>Translator</h1>
                      {editingName ? (
                        <input className="fname mono" autoFocus value={fileName}
                          style={{ fontFamily: "var(--mono)" }}
                          onChange={(e) => setFileName(e.target.value)}
                          onBlur={() => setEditingName(false)}
                          onKeyDown={(e) => { if (e.key === "Enter") setEditingName(false); }} />
                      ) : (
                        <span className="fname" onClick={() => setEditingName(true)}>
                          {fileName}
                          <span className="ed" title="Rename"><I.pencil size={13}/></span>
                        </span>
                      )}
                    </div>
                    <div className="tr-sub">
                      <span className="lang"><span className="flag">EN</span> English (en-US)</span>
                      <I.arrowRight size={14} className="arrow" />
                      <LangSelect value={tgtLang} onChange={setTgtLang} />
                      <span className="dotsep" />
                      <span className="orig">&lt;file original="{FILE.original}"&gt;</span>
                      <span className="dotsep" />
                      <span>{units.length} trans-units</span>
                    </div>
                  </div>
                  <div className="tr-actions">
                    <button className="btn" onClick={preTranslate}><I.zap/>Pre-translate from memory</button>
                    <button className="btn primary"><I.download/>Export .xlf</button>
                  </div>
                </div>
                <div style={{ marginTop: 18 }}><ProgressViz c={allCounts} variant="bar" /></div>
              </div>

              {/* toolbar */}
              <FilterBar filter={filter} setFilter={setFilter} counts={allCounts}
                hiddenCount={hiddenCount} search={search} setSearch={setSearch}
                kind={kind} setKind={setKind} view={view} setView={setView} />

              {/* body */}
              {filtered.length === 0 ? (
                <div className="tr-empty">
                  <span className="ei"><I.search size={20} /></span>
                  <div className="et">No units match</div>
                  <div className="es">Try a different filter, kind, or search term.</div>
                  <button className="btn sm" onClick={() => { setFilter("all"); setKind("All kinds"); setSearch(""); }}>Clear filters</button>
                </div>
              ) : view === "list" ? <LayoutA {...layoutProps} /> : <LayoutC {...layoutProps} />}
            </div>
          </div>
        </div>
      </div>

      <div className={"toast" + (toast ? " show" : "")}>
        <I.checkCircle/>{toast}
      </div>
    </>
  );
}

ReactDOM.createRoot(document.getElementById("root")).render(<App />);
