/* ============================================================
   Object Explorer screen
   ============================================================ */
const { useRef } = React;
const OBJ = [
  ["codeunit", 2, "Company-Initialize", "Base Application", "Microsoft.Foundation.Company", 807],
  ["codeunit", 3, "G/L Account-Indent", "Base Application", "Microsoft.Finance.GeneralLedger.Account", 178],
  ["codeunit", 6, "Fiscal Year-Close", "Base Application", "Microsoft.Foundation.Period", 81],
  ["codeunit", 7, "GLBudget-Open", "Base Application", "Microsoft.Finance.GeneralLedger.Budget", 90],
  ["codeunit", 8, "AccSchedManagement", "Base Application", "Microsoft.Finance.FinancialReports", 3480],
  ["table", 15, "G/L Account", "Base Application", "Microsoft.Finance.GeneralLedger.Account", 1204],
  ["codeunit", 10, "Type Helper", "Base Application", "System.Reflection", 1073],
  ["codeunit", 11, "Gen. Jnl.-Check Line", "Base Application", "Microsoft.Finance.GeneralLedger.Journal", 1715],
  ["codeunit", 12, "Gen. Jnl.-Post Line", "Base Application", "Microsoft.Finance.GeneralLedger.Posting", 10924],
  ["codeunit", 13, "Gen. Jnl.-Post Batch", "Base Application", "Microsoft.Finance.GeneralLedger.Posting", 2491],
  ["page", 39, "General Journal", "Base Application", "Microsoft.Finance.GeneralLedger.Journal", 642],
  ["codeunit", 15, "Gen. Jnl.-Show Card", "Base Application", "Microsoft.Finance.GeneralLedger.Journal", 92],
  ["codeunit", 17, "Gen. Jnl.-Post Reverse", "Base Application", "Microsoft.Finance.GeneralLedger.Reversal", 1511],
  ["report", 3, "G/L Trial Balance", "Base Application", "Microsoft.Finance.FinancialReports", 489],
  ["codeunit", 20, "Posting Preview Event Handler", "Base Application", "Microsoft.Finance.GeneralLedger.Preview", 1059],
  ["codeunit", 21, "Item Jnl.-Check Line", "Base Application", "Microsoft.Inventory.Journal", 731],
  ["codeunit", 22, "Item Jnl.-Post Line", "Base Application", "Microsoft.Inventory.Posting", 8674],
  ["codeunit", 23, "Item Jnl.-Post Batch", "Base Application", "Microsoft.Inventory.Posting", 1349],
  ["table", 27, "Item", "Base Application", "Microsoft.Inventory.Item", 2156],
  ["page", 30, "Item Card", "Base Application", "Microsoft.Inventory.Item", 1320],
  ["codeunit", 24, "Inventory Setup", "Base Application", "Microsoft.Inventory.Setup", 40],
  ["codeunit", 26, "Confirm Management Impl.", "System Application", "System.Utilities", 48],
  ["codeunit", 27, "Confirm Management", "System Application", "System.Utilities", 68],
];

const MAXLN = Math.max(...OBJ.map((o) => o[5]));
const lnPct = (n) => Math.max(7, (Math.log(n + 1) / Math.log(MAXLN + 1)) * 100);

// Versions available to diff the current object against.
const COMPARE_VERSIONS = [
  "BC 28.0.46291",
  "BC 27.5.41902",
  "BC 27.0.38104",
  "BC 26.4.33517",
  "Local workspace copy",
];

function nsParts(ns) {
  const i = ns.lastIndexOf(".");
  return i < 0 ? [ns, ""] : [ns.slice(0, i + 1), ns.slice(i + 1)];
}

function ObjectExplorer() {
  const [openRow, setOpenRow] = useState(null);
  const [pos, setPos] = useState(null);
  const [cmpOpen, setCmpOpen] = useState(false);
  const [toast, setToast] = useState(null);
  const tRef = useRef(null);

  const flash = (msg) => {
    setToast(msg);
    clearTimeout(tRef.current);
    tRef.current = setTimeout(() => setToast(null), 2000);
  };
  const close = () => { setOpenRow(null); setCmpOpen(false); };
  const openMenu = (i, e) => {
    const r = e.currentTarget.getBoundingClientRect();
    setPos({ top: r.bottom + 6, right: window.innerWidth - r.right });
    setCmpOpen(false);
    setOpenRow((cur) => (cur === i ? null : i));
  };

  useEffect(() => {
    if (openRow === null) return;
    const onDown = (e) => {
      if (!e.target.closest(".oe-menu") && !e.target.closest(".split-caret")) close();
    };
    const onMove = () => close();
    const page = document.querySelector(".page");
    document.addEventListener("mousedown", onDown);
    page && page.addEventListener("scroll", onMove, true);
    window.addEventListener("resize", onMove);
    return () => {
      document.removeEventListener("mousedown", onDown);
      page && page.removeEventListener("scroll", onMove, true);
      window.removeEventListener("resize", onMove);
    };
  }, [openRow]);

  return (
    <div className="page">
      <div className="page-inner">
        <div className="oe-head">
          <div>
            <h1>Business Central 28.1</h1>
            <div className="oe-meta">
              <span>120 module(s)</span>
              <span className="dotsep" style={{ width: 3, height: 3, borderRadius: 9, background: "var(--border-strong)" }}/>
              <span className="mono">BC 28.1.49838.49886</span>
              <span className="status-pill"><span className="sd"/>Ready</span>
            </div>
          </div>
          <div className="oe-actions">
            <button className="btn"><I.pkgPlus/>Add modules</button>
            <button className="btn"><I.arrowLeft/>Back to releases</button>
          </div>
        </div>

        {/* filters */}
        <div className="oe-filters">
          <div>
            <span className="flabel">Search in</span>
            <div className="select-wrap">
              <select className="select" defaultValue="obj">
                <option value="obj">Objects (Alt+1)</option>
                <option value="fields">Fields (Alt+2)</option>
                <option value="procs">Procedures (Alt+3)</option>
              </select>
              <I.chevDown/>
            </div>
          </div>
          <div>
            <span className="flabel">Search</span>
            <div className="oe-search">
              <I.search/>
              <input className="input" placeholder={'Name, ID, 50000..99999, sales*, "exact"'} />
            </div>
          </div>
          <div>
            <span className="flabel">Object type</span>
            <div className="select-wrap">
              <select className="select" defaultValue="all"><option value="all">All types</option><option>codeunit</option><option>table</option><option>page</option><option>report</option></select>
              <I.chevDown/>
            </div>
          </div>
          <div>
            <span className="flabel">Extension</span>
            <div className="select-wrap">
              <select className="select" defaultValue="all"><option value="all">All</option><option>Base Application</option><option>System Application</option></select>
              <I.chevDown/>
            </div>
          </div>
          <div>
            <span className="flabel">Namespace</span>
            <div className="select-wrap">
              <select className="select" defaultValue="all"><option value="all">All</option><option>Microsoft.Finance</option><option>Microsoft.Inventory</option></select>
              <I.chevDown/>
            </div>
          </div>
          <div>
            <span className="flabel" aria-hidden="true">&nbsp;</span>
            <button className="icon-btn" title="Reset filters" aria-label="Reset filters"><I.rotateCcw/></button>
          </div>
        </div>

        <div className="oe-count">Showing <b>100</b> of <b>14,619</b> object(s).</div>

        <div className="oe-table-wrap">
          <table className="oe-table">
            <thead>
              <tr>
                <th className="c-type">Type</th>
                <th className="c-id">ID</th>
                <th>Name</th>
                <th className="c-ext">Extension</th>
                <th>Namespace</th>
                <th className="c-actions">Actions</th>
              </tr>
            </thead>
            <tbody>
              {OBJ.map((o, i) => {
                const [type, id, name, ext, ns, lines] = o;
                const [head, tail] = nsParts(ns);
                return (
                  <tr key={i}>
                    <td className="c-type"><span className={"otype " + type}>{type}</span></td>
                    <td className="c-id"><span className="oid">{id}</span></td>
                    <td><span className="oname">{name}</span></td>
                    <td className="c-ext"><span className="oext">{ext}</span></td>
                    <td><span className="ons">{head}<span className="ns-tail">{tail}</span></span></td>
                    <td className="c-actions">
                      <div className="split">
                        <button className="split-main" onClick={() => flash("Opening reference · " + name)}>
                          <I.eye/>View reference
                        </button>
                        <button className={"split-caret" + (openRow === i ? " on" : "")} title="More actions"
                          onClick={(e) => openMenu(i, e)}>
                          <I.chevDown size={14}/>
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>

      {openRow !== null && pos && (
        <div className="oe-menu" style={{ position: "fixed", top: pos.top, right: pos.right }}>
          <button className="mi" onClick={() => { flash("Opening reference · " + OBJ[openRow][2]); close(); }}>
            <I.eye/>View reference
          </button>
          <button className={"mi has-sub" + (cmpOpen ? " open" : "")} onClick={() => setCmpOpen((v) => !v)}>
            <I.compare/>Compare with…<I.chevRight size={14} className="sub-caret"/>
          </button>
          {cmpOpen && (
            <div className="msub">
              {COMPARE_VERSIONS.map((v) => (
                <button key={v} className="mi sub" onClick={() => { flash("Comparing " + OBJ[openRow][2] + " ↔ " + v); close(); }}>
                  <span className="mono vtag">{v}</span>
                </button>
              ))}
            </div>
          )}
          <div className="mdiv"/>
          <button className="mi" onClick={() => { flash("Downloading source · " + OBJ[openRow][2] + ".al"); close(); }}>
            <I.download/>Download source
          </button>
        </div>
      )}

      <div className={"toast" + (toast ? " show" : "")}><I.checkCircle/>{toast}</div>
    </div>
  );
}

window.ObjectExplorer = ObjectExplorer;
