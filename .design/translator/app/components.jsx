/* ============================================================
   Shell (sidebar + topbar) and shared Translator widgets
   ============================================================ */
const { useState, useRef, useEffect } = React;

/* ---------- Language picker (target language) ---------- */
function LangSelect({ value, onChange }) {
  const [open, setOpen] = useState(false);
  const ref = useRef(null);
  useEffect(() => {
    const h = (e) => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
    document.addEventListener("mousedown", h);
    return () => document.removeEventListener("mousedown", h);
  }, []);
  return (
    <span className="langsel-wrap" ref={ref}>
      <button className="langbtn" onClick={() => setOpen((o) => !o)} title="Change target language">
        <span className="flag">{value.flag}</span>
        <span>{`${value.name} (${value.code})`}</span>
        <I.chevDown size={13} />
      </button>
      {open && (
        <div className="langmenu">
          <div className="langmenu-cap">Target language</div>
          {LANGS.map((l) => (
            <div key={l.code} className={"langopt" + (l.code === value.code ? " on" : "")}
              onClick={() => { onChange(l); setOpen(false); }}>
              <span className="flag">{l.flag}</span>
              <span className="lname">{l.name}</span>
              <span className="lcode mono">{l.code}</span>
              {l.code === value.code ? <I.check size={14} /> : <span />}
            </div>
          ))}
        </div>
      )}
    </span>
  );
}

/* ---------- Sidebar ---------- */
const NavItem = ({ icon, label, active, onClick }) => (
  <div className={"nav-item" + (active ? " active" : "")} onClick={onClick}>
    {icon}<span>{label}</span>
  </div>
);
const NavSub = ({ label, active }) => (
  <div className={"nav-sub" + (active ? " active" : "")}>{label}</div>
);

function Sidebar() {
  return (
    <nav className="nav">
      <div className="brand">
        <span className="logo">
          <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor"
               strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round">
            <path d="M14.5 4.5a4 4 0 0 0-5 5L4 15v5h5l5.5-5.5a4 4 0 0 0 5-5l-2.8 2.8-2.2-.6-.6-2.2z"/>
          </svg>
        </span>
        AL Dev Toolbox
      </div>

      <div className="nav-section">
        <NavItem icon={<I.home/>} label="Home" />
        <NavItem icon={<I.folderPlus/>} label="Projects" />
        <NavSub label="Workspace" />
        <NavSub label="Extension" />
        <NavItem icon={<I.code/>} label="Cookbook" />
        <NavItem icon={<I.box/>} label="Object Explorer" />
        <NavItem icon={<I.branch/>} label="Piper" />
        <NavItem icon={<I.bot/>} label="MCP" />
        <NavItem icon={<I.languages/>} label="Translator" active />
      </div>

      <div className="nav-divider" />

      <div className="nav-section">
        <div className="cap">Admin</div>
        <NavItem icon={<I.dashboard/>} label="Dashboard" />
        <NavItem icon={<I.layers/>} label="Templates" />
        <NavSub label="Defaults" />
        <NavSub label="Always-included files" />
        <NavSub label="Workspace settings" />
        <NavItem icon={<I.box/>} label="Modules" />
        <NavItem icon={<I.book/>} label="Catalogue" />
        <NavItem icon={<I.code/>} label="Cookbook" />
        <NavItem icon={<I.tag/>} label="App versions" />
        <NavItem icon={<I.fileCode/>} label="Object Explorer" />
        <NavItem icon={<I.settings/>} label="Administration" />
      </div>

      <div className="nav-divider" />

      <div className="nav-section">
        <div className="cap">Site administration</div>
        <NavItem icon={<I.users/>} label="All users" />
        <NavItem icon={<I.history/>} label="Audit log" />
        <NavItem icon={<I.database/>} label="Backup & storage" />
        <NavItem icon={<I.plug/>} label="Connections" />
        <NavItem icon={<I.monitor/>} label="Workers" />
        <NavItem icon={<I.settings/>} label="Settings" />
      </div>

      <div className="nav-foot"><I.github/></div>
    </nav>
  );
}

/* ---------- Topbar ---------- */
function Topbar({ theme, setTheme }) {
  return (
    <div className="topbar">
      <div className="theme-toggle">
        <button className={theme === "light" ? "on" : ""} onClick={() => setTheme("light")} title="Light"><I.sun/></button>
        <button className={theme === "system" ? "on" : ""} onClick={() => setTheme("system")} title="System"><I.monitor/></button>
        <button className={theme === "dark" ? "on" : ""} onClick={() => setTheme("dark")} title="Dark"><I.moon/></button>
      </div>
      <div className="user">
        <span className="uname">Mads Taanquist</span>
        <span className="uorg">(Consortio IT)</span>
        <span className="ubadge">Site admin</span>
        <div className="ulogout"><I.logout/></div>
      </div>
    </div>
  );
}

/* ---------- State helpers ---------- */
const STATE_LABEL = { untrans: "Needs translation", fuzzy: "Needs review", trans: "Translated", final: "Final" };
const STATE_SHORT = { untrans: "Needs", fuzzy: "Review", trans: "Translated", final: "Final" };

function StateBadge({ state, short }) {
  return (
    <span className={"state " + state}>
      <span className="sd" />
      {short ? STATE_SHORT[state] : STATE_LABEL[state]}
    </span>
  );
}

const simClass = (n) => (n >= 99 ? "exact" : n >= 80 ? "high" : "mid");
const simLabel = (n) => (n >= 99 ? "Exact" : n + "%");

/* ---------- Progress visualizations (the variation axis) ---------- */
function counts(units) {
  const c = { untrans: 0, fuzzy: 0, trans: 0, final: 0 };
  units.forEach((u) => { c[u.state]++; });
  c.total = units.length;
  c.done = c.trans + c.final;
  c.pct = Math.round((c.done / c.total) * 100);
  return c;
}

function ProgressViz({ c, variant }) {
  const pc = (n) => (n / c.total) * 100 + "%";
  if (variant === "ring") {
    const R = 24, CIRC = 2 * Math.PI * R;
    return (
      <div className="pviz-ring">
        <div className="ring">
          <svg width="58" height="58" viewBox="0 0 58 58">
            <circle cx="29" cy="29" r={R} fill="none" stroke="var(--surface-sunken)" strokeWidth="6" />
            <circle cx="29" cy="29" r={R} fill="none" stroke="var(--st-trans)" strokeWidth="6"
              strokeLinecap="round" strokeDasharray={CIRC}
              strokeDashoffset={CIRC * (1 - c.done / c.total)} />
          </svg>
          <div className="rt">{c.pct}<small>%</small></div>
        </div>
        <div className="pcounts" style={{ flexWrap: "wrap", gap: "10px 14px" }}>
          <span className="pcount"><span className="pdot f"/><span className="pn">{c.final}</span><span className="pl">final</span></span>
          <span className="pcount"><span className="pdot t"/><span className="pn">{c.trans}</span><span className="pl">translated</span></span>
          <span className="pcount"><span className="pdot z"/><span className="pn">{c.fuzzy}</span><span className="pl">review</span></span>
          <span className="pcount"><span className="pdot u"/><span className="pn">{c.untrans}</span><span className="pl">to do</span></span>
        </div>
      </div>
    );
  }
  if (variant === "min") {
    return (
      <div className="pviz-min">
        <div className="ptext"><b>{c.done}</b> / {c.total} translated <span className="muted">· {c.untrans + c.fuzzy} left · {c.pct}% done</span></div>
        <div className="uline"><span style={{ width: c.pct + "%" }} /></div>
      </div>
    );
  }
  // bar (default)
  return (
    <div className="pviz-bar">
      <div className="pbar">
        <span className="seg-f" style={{ width: pc(c.final) }} />
        <span className="seg-t" style={{ width: pc(c.trans) }} />
        <span className="seg-z" style={{ width: pc(c.fuzzy) }} />
        <span className="seg-u" style={{ width: pc(c.untrans) }} />
      </div>
      <div className="pbar-foot">
        <div className="pcounts">
          <span className="pcount"><span className="pdot f"/><span className="pn">{c.final}</span><span className="pl">final</span></span>
          <span className="pcount"><span className="pdot t"/><span className="pn">{c.trans}</span><span className="pl">translated</span></span>
          <span className="pcount"><span className="pdot z"/><span className="pn">{c.fuzzy}</span><span className="pl">review</span></span>
          <span className="pcount"><span className="pdot u"/><span className="pn">{c.untrans}</span><span className="pl">to do</span></span>
        </div>
        <div className="pct">{c.pct}% <small>done</small></div>
      </div>
    </div>
  );
}

/* ---------- Kind dropdown ---------- */
function KindSelect({ value, onChange }) {
  const [open, setOpen] = useState(false);
  const ref = useRef(null);
  useEffect(() => {
    const h = (e) => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
    document.addEventListener("mousedown", h);
    return () => document.removeEventListener("mousedown", h);
  }, []);
  return (
    <span className="kindsel-wrap" ref={ref}>
      <button className="kindsel" onClick={() => setOpen((o) => !o)}>
        <I.filter size={14} />{value}<I.chevDown size={14} />
      </button>
      {open && (
        <div className="kindmenu">
          {KINDS.map((k) => (
            <div key={k} className={"kindopt" + (k === value ? " on" : "")}
              onClick={() => { onChange(k); setOpen(false); }}>
              <span>{k}</span>
              {k === value ? <I.check size={14} /> : <span />}
            </div>
          ))}
        </div>
      )}
    </span>
  );
}

/* ---------- Filter bar ---------- */
function FilterBar({ filter, setFilter, counts, hiddenCount, search, setSearch,
                    kind, setKind, view, setView }) {
  return (
    <div className="tr-toolbar">
      <div className="ftabs">
        <button className={filter === "todo" ? "on" : ""} onClick={() => setFilter("todo")}>
          Needs translation <span className="cb">{counts.untrans + counts.fuzzy}</span>
        </button>
        <button className={filter === "all" ? "on" : ""} onClick={() => setFilter("all")}>
          All <span className="cb">{counts.total}</span>
        </button>
        <button className={filter === "done" ? "on" : ""} onClick={() => setFilter("done")}>
          Translated <span className="cb">{counts.done}</span>
        </button>
      </div>
      <KindSelect value={kind} onChange={setKind} />
      <div className="search">
        <I.search/>
        <input placeholder="Search source or target…" value={search} onChange={(e) => setSearch(e.target.value)} />
        {search && <button className="search-clear" onClick={() => setSearch("")} title="Clear"><I.x size={13} /></button>}
      </div>
      <div className="tb-spacer" />
      {hiddenCount > 0 && (
        <div className="hidden-badge"><b>{hiddenCount}</b> hidden by filter</div>
      )}
      <div className="viewtoggle" role="group" aria-label="View">
        <button className={view === "list" ? "on" : ""} onClick={() => setView("list")} title="Focused editor">
          <I.book size={15} />List
        </button>
        <button className={view === "grid" ? "on" : ""} onClick={() => setView("grid")} title="Compact grid — fast first pass">
          <I.dashboard size={15} />Grid
        </button>
      </div>
    </div>
  );
}

Object.assign(window, {
  Sidebar, Topbar, StateBadge, ProgressViz, FilterBar, LangSelect,
  counts, simClass, simLabel, STATE_LABEL,
});
