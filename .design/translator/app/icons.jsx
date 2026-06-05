/* ============================================================
   Icon set — Lucide-style stroke icons (inline SVG)
   ============================================================ */
const Ico = ({ d, paths, size = 18, fill = "none", vb = 24, ...p }) => (
  <svg width={size} height={size} viewBox={`0 0 ${vb} ${vb}`} fill={fill}
       stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" {...p}>
    {paths || <path d={d} />}
  </svg>
);

const I = {
  home:    (p) => <Ico {...p} paths={<><path d="M3 10.5 12 3l9 7.5"/><path d="M5 9.5V20a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V9.5"/><path d="M9.5 21v-6h5v6"/></>} />,
  folderPlus: (p) => <Ico {...p} paths={<><path d="M3 7a2 2 0 0 1 2-2h4l2 2h6a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><path d="M12 11v4M10 13h4"/></>} />,
  code:    (p) => <Ico {...p} paths={<><path d="m8 8-4 4 4 4"/><path d="m16 8 4 4-4 4"/></>} />,
  box:     (p) => <Ico {...p} paths={<><path d="M21 8 12 3 3 8v8l9 5 9-5z"/><path d="m3 8 9 5 9-5"/><path d="M12 13v8"/></>} />,
  branch:  (p) => <Ico {...p} paths={<><circle cx="6" cy="6" r="2.4"/><circle cx="6" cy="18" r="2.4"/><circle cx="18" cy="7" r="2.4"/><path d="M6 8.4v7.2M8.4 6.6H14a3 3 0 0 1 0 0c1.6 0 1.6 0 1.6 0"/><path d="M18 9.4c0 4-3 4.6-12 6.2"/></>} />,
  bot:     (p) => <Ico {...p} paths={<><rect x="4" y="8" width="16" height="11" rx="2.5"/><path d="M12 8V4M9 4h6"/><circle cx="9" cy="13" r="1"/><circle cx="15" cy="13" r="1"/></>} />,
  languages: (p) => <Ico {...p} paths={<><path d="M4 5h8M8 3v2"/><path d="M10 5c0 4-3 7-7 8"/><path d="M5 9c0 2 2 3.5 5 4.5"/><path d="m12 21 4-9 4 9"/><path d="M13.5 18h5"/></>} />,
  dashboard: (p) => <Ico {...p} paths={<><rect x="3" y="3" width="7" height="9" rx="1.5"/><rect x="14" y="3" width="7" height="5" rx="1.5"/><rect x="14" y="12" width="7" height="9" rx="1.5"/><rect x="3" y="16" width="7" height="5" rx="1.5"/></>} />,
  layers:  (p) => <Ico {...p} paths={<><path d="m12 3 9 5-9 5-9-5z"/><path d="m3 13 9 5 9-5"/></>} />,
  book:    (p) => <Ico {...p} paths={<><path d="M4 5a2 2 0 0 1 2-2h13v16H6a2 2 0 0 0-2 2z"/><path d="M4 5v14"/></>} />,
  tag:     (p) => <Ico {...p} paths={<><path d="M3 12V5a2 2 0 0 1 2-2h7l9 9-7 7z"/><circle cx="7.5" cy="7.5" r="1.3"/></>} />,
  fileCode:(p) => <Ico {...p} paths={<><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/><path d="m9 13-1.5 1.5L9 16M14 13l1.5 1.5L14 16"/></>} />,
  settings:(p) => <Ico {...p} paths={<><circle cx="12" cy="12" r="3"/><path d="M19.4 14a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-2.7 1.1V20a2 2 0 0 1-4 0v-.1A1.6 1.6 0 0 0 6.8 18a1.6 1.6 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1A1.6 1.6 0 0 0 2.3 13H2a2 2 0 0 1 0-4h.1A1.6 1.6 0 0 0 4 6.8a1.6 1.6 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1A1.6 1.6 0 0 0 9 2.3V2a2 2 0 0 1 4 0v.1A1.6 1.6 0 0 0 17.2 4a1.6 1.6 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.6 1.6 0 0 0-.3 1.8V9a2 2 0 0 1 0 4z"/></>} />,
  users:   (p) => <Ico {...p} paths={<><circle cx="9" cy="8" r="3"/><path d="M3 20a6 6 0 0 1 12 0"/><path d="M16 5.5a3 3 0 0 1 0 5M21 20a6 6 0 0 0-4-5.6"/></>} />,
  history: (p) => <Ico {...p} paths={<><path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 4v4h4"/><path d="M12 8v4l3 2"/></>} />,
  database:(p) => <Ico {...p} paths={<><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/><path d="M4 11v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6"/></>} />,
  plug:    (p) => <Ico {...p} paths={<><path d="M9 3v6M15 3v6"/><path d="M7 9h10v3a5 5 0 0 1-10 0z"/><path d="M12 17v4"/></>} />,
  monitor: (p) => <Ico {...p} paths={<><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M9 20h6M12 16v4"/></>} />,
  sun:     (p) => <Ico {...p} paths={<><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></>} />,
  moon:    (p) => <Ico {...p} paths={<><path d="M21 12.8A8.5 8.5 0 1 1 11.2 3a6.5 6.5 0 0 0 9.8 9.8z"/></>} />,
  logout:  (p) => <Ico {...p} paths={<><path d="M9 21H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3"/><path d="m16 17 5-5-5-5M21 12H9"/></>} />,
  search:  (p) => <Ico {...p} paths={<><circle cx="11" cy="11" r="7"/><path d="m21 21-3.5-3.5"/></>} />,
  pencil:  (p) => <Ico {...p} paths={<><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/></>} />,
  arrowRight:(p) => <Ico {...p} paths={<><path d="M5 12h14M13 6l6 6-6 6"/></>} />,
  chevDown:(p) => <Ico {...p} paths={<><path d="m6 9 6 6 6-6"/></>} />,
  chevUp:  (p) => <Ico {...p} paths={<><path d="m6 15 6-6 6 6"/></>} />,
  up:      (p) => <Ico {...p} paths={<><path d="M12 19V5M6 11l6-6 6 6"/></>} />,
  down:    (p) => <Ico {...p} paths={<><path d="M12 5v14M6 13l6 6 6-6"/></>} />,
  check:   (p) => <Ico {...p} paths={<><path d="m20 6-11 11-5-5"/></>} />,
  checkCircle:(p) => <Ico {...p} paths={<><circle cx="12" cy="12" r="9"/><path d="m8.5 12 2.5 2.5 4.5-5"/></>} />,
  zap:     (p) => <Ico {...p} paths={<><path d="M13 2 4 14h7l-1 8 9-12h-7z"/></>} />,
  download:(p) => <Ico {...p} paths={<><path d="M12 3v12M7 11l5 5 5-5"/><path d="M5 21h14"/></>} />,
  note:    (p) => <Ico {...p} paths={<><path d="M9 5H6a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-9l-5-5z"/><path d="M14 5v5h5M8 14h7M8 17h5"/></>} />,
  filter:  (p) => <Ico {...p} paths={<><path d="M3 5h18l-7 8v6l-4-2v-4z"/></>} />,
  database2:(p) => <Ico {...p} paths={<><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/></>} />,
  x:       (p) => <Ico {...p} paths={<><path d="M18 6 6 18M6 6l12 12"/></>} />,
  github:  (p) => <Ico {...p} paths={<><path d="M9 19c-4 1.5-4-2-6-2.5M15 21v-3.2a2.8 2.8 0 0 0-.8-2.2c2.6-.3 5.3-1.3 5.3-5.8a4.5 4.5 0 0 0-1.2-3.1 4.2 4.2 0 0 0-.1-3.1s-1-.3-3.3 1.2a11 11 0 0 0-6 0C6.6 3.7 5.6 4 5.6 4a4.2 4.2 0 0 0-.1 3.1A4.5 4.5 0 0 0 4.3 10c0 4.5 2.7 5.5 5.3 5.8a2.8 2.8 0 0 0-.8 2.1V21"/></>} />,
};

window.I = I;
