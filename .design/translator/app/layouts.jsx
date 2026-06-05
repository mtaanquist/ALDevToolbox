/* ============================================================
   Three editor layouts: A (detail rail), B (Poedit dock), C (inline grid)
   Shared building blocks at top.
   ============================================================ */

/* ---- shared blocks ---- */
function SourceBlock({ u }) {
  return (
    <div>
      <div className="field-label">
        <span className="cap-label">Source · English</span>
        <span className="kind">{u.kind}</span>
      </div>
      <div className="src-box">{u.src}</div>
      {u.note && (
        <div className="note-box" style={{ marginTop: 10 }}>{u.note}</div>
      )}
    </div>
  );
}

function StatePicker({ u, onState }) {
  const opts = [["untrans", "Needs translation"], ["trans", "Translated"], ["final", "Final"]];
  return (
    <div className="state-pick">
      {opts.map(([k, label]) => (
        <button key={k} className={(u.state === k || (k === "untrans" && u.state === "fuzzy") ? "on " : "") + k}
          onClick={() => onState(u.id, k)}>
          <span className="sd" />{label}
        </button>
      ))}
    </div>
  );
}

function TargetBlock({ u, onTarget, onState, autoFocus, tgtLang }) {
  return (
    <div>
      <div className="field-label">
        <span className="cap-label">Target · {tgtLang ? `${tgtLang.name} (${tgtLang.code})` : "Danish (da-DK)"}</span>
        <StateBadge state={u.state} short />
      </div>
      <textarea className="tgt-area" value={u.tgt} autoFocus={autoFocus}
        placeholder={tgtLang ? `Type the ${tgtLang.name} translation…` : "Type the translation…"}
        onChange={(e) => onTarget(u.id, e.target.value)} />
      <div style={{ marginTop: 10 }}><StatePicker u={u} onState={onState} /></div>
    </div>
  );
}

function SuggestionsBlock({ u, onFill, numbered }) {
  return (
    <div>
      <div className="sugg-head">
        <span className="cap-label">Suggestions</span>
        <span className="mem-pill">Translation memory</span>
      </div>
      <div className="sugg-list">
        {(!u.suggs || u.suggs.length === 0) && (
          <div className="sugg-empty">No memory matches yet — your translation will seed it.</div>
        )}
        {(u.suggs || []).map((sg, i) => (
          <div className="sugg" key={i} onClick={() => onFill(u.id, sg.text)}>
            {numbered ? <span className="skey">{i + 1}</span> : <span />}
            <div>
              <div className="stext">{sg.text}</div>
              <div className="smeta">
                <span className="sorigin"><I.database2 size={12}/>{sg.origin}</span>
              </div>
            </div>
            <span className={"ssim " + simClass(sg.sim)}>{simLabel(sg.sim)}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

function KbdLegend() {
  const rows = [
    ["Save · set translated · next", <kbd>Alt</kbd>, <kbd>↵</kbd>],
    ["Previous / next unit", <kbd>Alt</kbd>, <kbd>↑</kbd>, <kbd>↓</kbd>],
    ["Apply suggestion 1–9", <kbd>Alt</kbd>, <kbd>1</kbd>, <kbd>…</kbd>],
  ];
  return (
    <div className="kbd-legend">
      {rows.map((r, i) => (
        <div className="klrow" key={i}>
          <span style={{ color: "var(--ink-3)" }}>{r[0]}</span>
          <span>{r.slice(1).map((k, j) => <React.Fragment key={j}>{k}</React.Fragment>)}</span>
        </div>
      ))}
    </div>
  );
}

/* ---- LIST ROW (Layout A) ---- */
function UnitRow({ u, sel, onSelect }) {
  return (
    <div className={"urow" + (sel ? " sel" : "")} onClick={() => onSelect(u.id)}>
      <div className="ust"><span className={"sd " + u.state} /></div>
      <div className="ubody">
        <div className="umeta">
          <span className="kind">{u.kind}</span>
          <span className="id-mono">{u.short}</span>
        </div>
        <div className="usrc">{u.src}</div>
        {u.tgt
          ? <div className="utgt">{u.tgt}</div>
          : <div className="utgt empty">Needs translation</div>}
      </div>
    </div>
  );
}

/* ============================================================
   LAYOUT A — list + sticky detail rail
   ============================================================ */
function LayoutA({ units, sel, selId, onSelect, onTarget, onState, onFill, onNav, tgtLang }) {
  const idx = units.findIndex((u) => u.id === selId);
  return (
    <div className="la">
      <div className="la-list">
        <div className="ulist">
          {units.map((u) => <UnitRow key={u.id} u={u} sel={u.id === selId} onSelect={onSelect} />)}
        </div>
      </div>
      <div className="la-rail">
        {sel ? (
          <div className="detail">
            <div className="dhead">
              <span className="dunit">{idx + 1} of {units.length} · {sel.short}</span>
              <div className="dnav">
                <button onClick={() => onNav(-1)} title="Previous (Alt+↑)"><I.up size={15}/></button>
                <button onClick={() => onNav(1)} title="Next (Alt+↓)"><I.down size={15}/></button>
              </div>
            </div>
            <SourceBlock u={sel} />
            <TargetBlock u={sel} onTarget={onTarget} onState={onState} tgtLang={tgtLang} />
            <SuggestionsBlock u={sel} onFill={onFill} numbered />
            <KbdLegend />
          </div>
        ) : <div className="detail"><div className="sugg-empty">Select a unit to translate.</div></div>}
      </div>
    </div>
  );
}

/* ============================================================
   LAYOUT B — Poedit dock (table over docked editor)
   ============================================================ */
function LayoutB({ units, sel, selId, onSelect, onTarget, onState, onFill, onNav, tgtLang }) {
  const idx = units.findIndex((u) => u.id === selId);
  return (
    <div className="lb">
      <div className="lb-table-wrap">
        <table className="xtable">
          <thead>
            <tr>
              <th></th><th>Kind</th><th>Source</th><th>Target</th><th>State</th>
            </tr>
          </thead>
          <tbody>
            {units.map((u) => (
              <tr key={u.id} className={u.id === selId ? "sel" : ""} onClick={() => onSelect(u.id)}>
                <td className="c-id" style={{ width: 26 }}>
                  <span className={"sd"} style={{ display: "inline-block", width: 9, height: 9, borderRadius: 9,
                    background: `var(--st-${u.state === "fuzzy" ? "fuzzy" : u.state === "untrans" ? "untrans" : u.state === "trans" ? "trans" : "final"})` }} />
                </td>
                <td className="c-kind"><span className="kind">{u.kind}</span></td>
                <td className="c-src">{u.src}</td>
                <td className={"c-tgt" + (u.tgt ? "" : " empty")}>{u.tgt || "Needs translation"}</td>
                <td className="c-state"><StateBadge state={u.state} short /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="lb-dock">
        {sel ? (
          <>
            <div className="ddl">
              <div className="ddhead">
                <span className="dunit">{sel.short}</span>
                <div className="dnav" style={{ display: "flex", gap: 6 }}>
                  <button className="btn sm ghost" onClick={() => onNav(-1)}><I.up size={14}/></button>
                  <button className="btn sm ghost" onClick={() => onNav(1)}><I.down size={14}/></button>
                </div>
              </div>
              <SourceBlock u={sel} />
            </div>
            <div className="ddr">
              <TargetBlock u={sel} onTarget={onTarget} onState={onState} autoFocus tgtLang={tgtLang} />
              <SuggestionsBlock u={sel} onFill={onFill} numbered />
            </div>
          </>
        ) : (
          <div className="ddl"><div className="sugg-empty">Select a row to edit its translation. <kbd>Alt</kbd> <kbd>↓</kbd> to walk units.</div></div>
        )}
      </div>
    </div>
  );
}

/* ============================================================
   LAYOUT C — inline grid with expanding suggestion row
   ============================================================ */
function LayoutC({ units, selId, onSelect, onTarget, onFill, tgtLang }) {
  return (
    <div className="lc">
      <table className="cgrid">
        <thead>
          <tr>
            <th style={{ width: 30 }}></th>
            <th style={{ width: 150 }}>Unit</th>
            <th>Source · English</th>
            <th style={{ width: "38%" }}>Target · {tgtLang ? tgtLang.name : "Danish"}</th>
          </tr>
        </thead>
        <tbody>
          {units.map((u) => {
            const active = u.id === selId;
            return (
              <React.Fragment key={u.id}>
                <tr className={"crow" + (active ? " active" : "")} onClick={() => onSelect(u.id)}>
                  <td className="cc-st"><span className={"sd " + u.state} /></td>
                  <td className="cc-meta">
                    <span className="id-mono">{u.short}</span>
                    <StateBadge state={u.state} short />
                  </td>
                  <td className="cc-src">{u.src}</td>
                  <td className="cc-tgt">
                    <textarea className="cell-input" rows={1} value={u.tgt}
                      placeholder="Needs translation…"
                      onFocus={() => onSelect(u.id)}
                      onChange={(e) => onTarget(u.id, e.target.value)} />
                  </td>
                </tr>
                {active && (
                  <tr className="cexp">
                    <td colSpan={4}>
                      <div className="cexp-inner">
                        {u.note && <div className="cnote"><I.note size={14}/>{u.note}</div>}
                        <div className="csuggs">
                          <span className="cs-label">Memory</span>
                          {(!u.suggs || u.suggs.length === 0) && <span className="sugg-empty" style={{ padding: 0 }}>No matches yet.</span>}
                          {(u.suggs || []).map((sg, i) => (
                            <div className="cchip" key={i} onClick={() => onFill(u.id, sg.text)}>
                              <span className="ck">{i + 1}</span>
                              <span className="ctext">{sg.text}</span>
                              <span className={"csim " + simClass(sg.sim)}>{simLabel(sg.sim)}</span>
                              <span className="corigin">{sg.origin.split(" · ")[0]}</span>
                            </div>
                          ))}
                        </div>
                      </div>
                    </td>
                  </tr>
                )}
              </React.Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

Object.assign(window, { LayoutA, LayoutB, LayoutC });
