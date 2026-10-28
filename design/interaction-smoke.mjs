/**
 * DOM-free interaction smoke: loads app.js against a minimal document stub,
 * drives the same control IDs the UI uses, asserts state transitions.
 * No network, no browser install.
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const failures = [];
const passes = [];

function ok(m) { passes.push(m); }
function bad(m) { failures.push(m); }

function el(tag, attrs = {}, kids = []) {
  const node = {
    tagName: tag.toUpperCase(),
    attrs: { ...attrs },
    children: kids,
    style: {},
    classList: {
      _s: new Set((attrs.class || "").split(/\s+/).filter(Boolean)),
      add(c) { this._s.add(c); node.attrs.class = [...this._s].join(" "); },
      remove(c) { this._s.delete(c); node.attrs.class = [...this._s].join(" "); },
      toggle(c, force) {
        if (force === true) this.add(c);
        else if (force === false) this.remove(c);
        else if (this._s.has(c)) this.remove(c); else this.add(c);
      },
      contains(c) { return this._s.has(c); },
    },
    listeners: {},
    value: attrs.value || "",
    checked: !!attrs.checked,
    textContent: attrs.textContent || "",
    disabled: false,
    setAttribute(k, v) { this.attrs[k] = String(v); },
    getAttribute(k) { return this.attrs[k] ?? null; },
    addEventListener(type, fn) {
      (this.listeners[type] ||= []).push(fn);
    },
    click() {
      for (const fn of this.listeners.click || []) fn({ preventDefault() {}, target: this });
    },
    focus() {},
    querySelector(sel) { return query(this, sel); },
    querySelectorAll(sel) { return queryAll(this, sel); },
  };
  return node;
}

function matches(node, sel) {
  if (sel.startsWith("#")) return node.attrs.id === sel.slice(1);
  if (sel.startsWith(".")) return node.classList.contains(sel.slice(1));
  if (sel.includes("[")) {
    const m = sel.match(/^(\w+)?\[([^\]]+)\]/);
    if (!m) return false;
    if (m[1] && node.tagName !== m[1].toUpperCase()) return false;
    const body = m[2];
    const eq = body.match(/([\w-]+)=["']?([^"'\]]+)["']?/);
    if (eq) return node.attrs[eq[1]] === eq[2];
    return node.attrs[body] != null || Object.prototype.hasOwnProperty.call(node.attrs, body);
  }
  if (sel.includes(" ")) {
    // not needed deeply
  }
  return node.tagName === sel.toUpperCase();
}

function walk(node, out = []) {
  out.push(node);
  for (const c of node.children || []) walk(c, out);
  return out;
}

function query(root, sel) {
  return queryAll(root, sel)[0] || null;
}
function queryAll(root, sel) {
  // support comma? no. support ".a .b"? simple split last
  const parts = sel.trim().split(/\s+/);
  let set = walk(root);
  for (const part of parts) {
    if (part === "") continue;
    const next = [];
    for (const n of set) {
      for (const c of walk(n).slice(1)) {
        if (matches(c, part)) next.push(c);
      }
      if (matches(n, part) && n !== root) next.push(n);
    }
    // also match root itself for #id queries on document
    if (matches(root, part)) next.push(root);
    set = [...new Set(next)];
  }
  return set;
}

const ids = {};
function byId(id) {
  if (!ids[id]) ids[id] = el("div", { id });
  return ids[id];
}

// Build stub DOM covering selectors used in app.js
const documentStub = el("document", { id: "document" });
const body = el("body");
documentStub.children = [body];

const neededIds = [
  "toast","connPill","chatEmpty","chatError","chatLoading","messages","identity",
  "panelHost","advancedToggle","advancedToggleSettings","modeLabel","needsBadge",
  "profileHidden","mainAppChrome","terminalDrawer","composerForm","composerInput",
  "btnNeedsYou","btnGrantFolder","confirmGrant","btnRevokeFolder","btnOpenPanel",
  "btnTerminal","confirmTerminal","closeTerminal","btnCheckpoint","confirmCheckpoint",
  "btnPush","confirmPush","btnWorktree","confirmWorktree","btnSecretWarn",
  "approveNeeds","btnHideProfile","btnSelectProfile",
];
for (const id of neededIds) {
  const n = byId(id);
  body.children.push(n);
}

// nav buttons
for (const nav of ["welcome","chat","bots","inbox","project","settings"]) {
  const b = el("button", { "data-nav": nav });
  b.dataset = { nav };
  Object.defineProperty(b, "dataset", { value: { nav }, writable: true });
  body.children.push(b);
}
// Fix dataset for query [data-nav]
body.children.filter(c => c.attrs["data-nav"]).forEach(c => {
  c.dataset = { nav: c.attrs["data-nav"] };
});

// panel tabs / panes / scenarios / modals / advanced-only / content
for (const tab of ["files","preview","changes"]) {
  const b = el("button", { "data-tab": tab });
  b.dataset = { tab };
  body.children.push(b);
  const pane = el("div", { class: "panel-pane", "data-pane": tab });
  pane.dataset = { pane: tab };
  body.children.push(pane);
}
for (const s of ["grant","diff","checkpoint","push","worktree","secret","needsyou","disconnected","loading","empty","error","profilehide","reconnect"]) {
  const b = el("button", { "data-scenario": s });
  b.dataset = { scenario: s };
  body.children.push(b);
}
for (const id of ["modalGrant","modalCheckpoint","modalPush","modalWorktree","modalSecret","modalNeedsYou","modalTerminal"]) {
  const m = byId(id);
  m.classList.add("modal-backdrop");
  body.children.push(m);
  const innerBtn = el("button");
  m.children.push(innerBtn);
}
const closeBtn = el("button", { "data-close": "" });
body.children.push(closeBtn);
const adv = el("div", { class: "advanced-only" });
body.children.push(adv);
for (const v of ["welcome","chat","bots","inbox","project","settings"]) {
  const sec = el("section", { class: "content", "data-view": v });
  sec.dataset = { view: v };
  body.children.push(sec);
}

// Wire dataset on content/panel for querySelectorAll(".content") etc. via class
walk(documentStub).forEach(n => {
  if (n.attrs && n.attrs["data-view"]) n.dataset = { ...(n.dataset||{}), view: n.attrs["data-view"] };
  if (n.attrs && n.attrs["data-pane"]) n.dataset = { ...(n.dataset||{}), pane: n.attrs["data-pane"] };
  if (n.attrs && n.attrs["data-tab"]) n.dataset = { ...(n.dataset||{}), tab: n.attrs["data-tab"] };
  if (n.attrs && n.attrs["data-scenario"]) n.dataset = { ...(n.dataset||{}), scenario: n.attrs["data-scenario"] };
  if (n.attrs && n.attrs["data-nav"]) n.dataset = { ...(n.dataset||{}), nav: n.attrs["data-nav"] };
});

const windowStub = {
  setTimeout: (fn) => { fn(); return 0; },
  clearTimeout: () => {},
};

const docApi = {
  querySelector(sel) {
    if (sel.startsWith("#")) return byId(sel.slice(1));
    return query(documentStub, sel);
  },
  querySelectorAll(sel) {
    if (sel.startsWith("#")) {
      const n = byId(sel.slice(1));
      return n ? [n] : [];
    }
    // special: .nav [data-nav]
    if (sel === ".nav [data-nav]" || sel === ".nav [data-nav]") {
      return walk(documentStub).filter(n => n.attrs && n.attrs["data-nav"]);
    }
    if (sel === "[data-scenario]") return walk(documentStub).filter(n => n.attrs && n.attrs["data-scenario"]);
    if (sel === "[data-close]") return walk(documentStub).filter(n => n.attrs && Object.prototype.hasOwnProperty.call(n.attrs, "data-close"));
    if (sel === ".modal-backdrop") return walk(documentStub).filter(n => n.classList.contains("modal-backdrop"));
    if (sel === ".advanced-only") return walk(documentStub).filter(n => n.classList.contains("advanced-only"));
    if (sel === ".content") return walk(documentStub).filter(n => n.classList.contains("content"));
    if (sel === ".panel-pane") return walk(documentStub).filter(n => n.classList.contains("panel-pane"));
    if (sel === ".panel-tabs button") return walk(documentStub).filter(n => n.attrs && n.attrs["data-tab"]);
    return queryAll(documentStub, sel);
  },
  addEventListener(type, fn) {
    (docApi._l ||= {})[type] ||= [];
    docApi._l[type].push(fn);
  },
};

// Ensure getElement paths used via $ = querySelector on document
const context = {
  console,
  window: windowStub,
  document: docApi,
  setTimeout: windowStub.setTimeout,
  clearTimeout: windowStub.clearTimeout,
};

const code = fs.readFileSync(path.join(__dirname, "assets", "app.js"), "utf8");
vm.runInNewContext(code, context, { filename: "app.js" });

function clickId(id) {
  const n = byId(id);
  if (!n.listeners.click) bad(`no click listener on #${id}`);
  else { n.click(); ok(`clicked #${id}`); }
}

function clickScenario(name) {
  const nodes = walk(documentStub).filter(n => n.dataset && n.dataset.scenario === name);
  if (!nodes.length) { bad(`scenario ${name} missing`); return; }
  nodes[0].click();
  ok(`scenario ${name}`);
}

// Drive flows — Advanced uses `change`, not `click`
{
  const n = byId("advancedToggle");
  n.checked = true;
  const fns = n.listeners.change || [];
  if (!fns.length) bad("no change listener on #advancedToggle");
  else {
    for (const fn of fns) fn.call(n, { preventDefault() {}, target: n });
    ok("toggled #advancedToggle via change");
  }
  if (byId("modeLabel").textContent === "Advanced") ok("Advanced label"); else bad("Advanced label not updated");
}

clickScenario("grant");
clickId("confirmGrant");
if (byId("identity").textContent.includes("DemoRepo")) ok("identity shows grant path"); else bad("identity missing path");

clickScenario("diff");
if (byId("panelHost").classList.contains("open")) ok("panel open after diff"); else bad("panel not open");

clickId("btnCheckpoint");
clickId("confirmCheckpoint");
if (byId("toast").textContent.includes("not pushed") || byId("toast").textContent.includes("Checkpoint")) ok("checkpoint toast"); else bad(`checkpoint toast: ${byId("toast").textContent}`);

clickId("btnPush");
clickId("confirmPush");
if (byId("toast").textContent.includes("no network") || byId("toast").textContent.includes("Push")) ok("push toast"); else bad(`push toast: ${byId("toast").textContent}`);

clickId("btnTerminal");
clickId("confirmTerminal");
if (byId("terminalDrawer").classList.contains("open")) ok("terminal open after opt-in"); else bad("terminal not open");

clickScenario("profilehide");
if (byId("profileHidden").classList.contains("hidden") === false) ok("profile gate visible"); else bad("profile gate not shown");

clickScenario("needsyou");
clickId("approveNeeds");

clickScenario("error");
if (byId("connPill").textContent.toLowerCase().includes("error") || byId("connPill").classList.contains("err")) ok("error connection state"); else bad("error state missing");

clickScenario("reconnect");

const out = [
  `PASS ${passes.length}`,
  ...passes.map(p => `  ✓ ${p}`),
  `FAIL ${failures.length}`,
  ...failures.map(f => `  ✗ ${f}`),
  failures.length ? "RESULT: FAIL" : "RESULT: ALL PASS",
].join("\n");
fs.writeFileSync(path.join(__dirname, "interaction-smoke-results.txt"), out, "utf8");
console.log(out);
process.exit(failures.length ? 1 : 0);
