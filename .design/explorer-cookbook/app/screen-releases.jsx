/* ============================================================
   Object Explorer — landing (imported reference packages)
   Reimagined from a flat table into a featured "latest import"
   panel + version-grouped package cards with file-count meters.
   ============================================================ */

const REL_TABS = [
  { id: "microsoft",  label: "Microsoft",  icon: "layersList" },
  { id: "thirdparty", label: "Third-party", icon: "box" },
  { id: "customer",   label: "Customer",   icon: "users" },
];

function relAgo(iso) {
  const now = new Date("2026-06-15T00:00:00");
  const d = new Date(iso + "T00:00:00");
  const days = Math.round((now - d) / 86400000);
  if (days <= 0) return "today";
  if (days === 1) return "yesterday";
  if (days < 30) return days + " days ago";
  const months = Math.round(days / 30);
  return months <= 1 ? "1 month ago" : months + " months ago";
}

function relMajor(label) {
  const m = label.match(/(\d+)\./);
  return m ? m[1] : "—";
}

function relGroups(source, list) {
  if (source !== "microsoft") return [{ title: null, items: list }];
  const groups = {};
  list.forEach((r) => {
    const k = relMajor(r.label);
    (groups[k] = groups[k] || []).push(r);
  });
  return Object.keys(groups)
    .sort((a, b) => Number(b) - Number(a))
    .map((k) => ({ title: "Version " + k, items: groups[k] }));
}

function ReleaseCard({ r, onOpen }) {
  return (
    <button className="rel-card" onClick={onOpen}>
      <div className="rc-top">
        <div>
          <div className="rc-name">{r.label}</div>
          <div className="rc-ver mono">{r.ver}</div>
        </div>
        <span className="rc-arrow"><I.arrowRight size={17} /></span>
      </div>
      <div className="rc-foot">
        <span className="rc-files"><b>{r.files.toLocaleString()}</b> files</span>
        <span className="rc-date"><I.calendar size={13} />{relAgo(r.imported)}</span>
      </div>
    </button>
  );
}

function Releases({ onOpenRelease }) {
  const [tab, setTab] = useState("microsoft");
  const list = REL_SOURCES[tab];
  const featured = list.find((r) => r.latest) || list[0];
  const rest = list.filter((r) => r !== featured);
  const totalFiles = list.reduce((a, r) => a + r.files, 0);

  return (
    <div className="page">
      <div className="page-inner">
        <div className="page-head">
          <h1>Object Explorer</h1>
          <div className="sub">Browse the AL source of every reference package you've imported. Pick a release to search its objects, fields and procedures — or compare any object across versions.</div>
        </div>

        {/* source tabs */}
        <div className="src-tabs" role="tablist">
          {REL_TABS.map((t) => (
            <button key={t.id} className={tab === t.id ? "on" : ""} onClick={() => setTab(t.id)} role="tab" aria-selected={tab === t.id}>
              {I[t.icon]()}{t.label}
              <span className="src-n">{REL_SOURCES[t.id].length}</span>
            </button>
          ))}
        </div>

        {list.length === 0 ? (
          <div className="rel-empty">
            <div className="rel-empty-ico"><I.box size={26} /></div>
            <div className="rel-empty-h">No customer packages imported yet</div>
            <div className="rel-empty-p">Import a customer's compiled app or symbols package to explore its objects here, side-by-side with the Microsoft base application.</div>
            <button className="btn primary"><I.pkgPlus />Import a package</button>
          </div>
        ) : (
          <>
            {/* featured latest */}
            {featured && (
              <div className="rel-hero">
                <div className="rh-main">
                  <div className="rh-cap">
                    <span className="cap-label">Latest import</span>
                    <span className="status-pill"><span className="sd" />Ready</span>
                  </div>
                  <h2 className="rh-title">{featured.label}</h2>
                  <div className="rh-meta">
                    <span className="mono">{featured.ver}</span>
                    {featured.pub && <><span className="dotsep" />{featured.pub}</>}
                    <span className="dotsep" />
                    <span className="rh-date"><I.calendar size={14} /><span>Imported {featured.imported} · {relAgo(featured.imported)}</span></span>
                  </div>
                </div>
                <div className="rh-side">
                  <div className="rh-stat">
                    <span className="rh-num">{featured.files.toLocaleString()}</span>
                    <span className="rh-lbl">Files indexed</span>
                  </div>
                  <div className="rh-actions">
                    <button className="btn primary lg" onClick={() => onOpenRelease(featured)}><I.search />Explore objects</button>
                    <button className="btn lg"><I.compare />Compare</button>
                  </div>
                </div>
              </div>
            )}

            <div className="rel-count">
              <b>{list.length}</b> release{list.length === 1 ? "" : "s"} imported · <b>{totalFiles.toLocaleString()}</b> files indexed
            </div>

            {/* version-grouped cards */}
            <div className="rel-timeline">
              {relGroups(tab, rest).map((g, gi) => (
                <div className="rel-group" key={gi}>
                  {g.title && (
                    <div className="rel-group-head">
                      <span className="rg-dot" />
                      <span className="rg-title">{g.title}</span>
                      <span className="rg-line" />
                      <span className="rg-count">{g.items.length} release{g.items.length === 1 ? "" : "s"}</span>
                    </div>
                  )}
                  <div className="rel-cards">
                    {g.items.map((r, i) => (
                      <ReleaseCard key={i} r={r} onOpen={() => onOpenRelease(r)} />
                    ))}
                  </div>
                </div>
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  );
}

window.Releases = Releases;
