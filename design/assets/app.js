(function () {
  "use strict";

  var state = {
    view: "welcome",
    advanced: false,
    profileSelected: true,
    folderGranted: false,
    terminalOptIn: false,
    panelOpen: false,
    panelTab: "files",
    terminalOpen: false,
    needsYou: 1,
    connection: "connected", // connected | disconnected | loading | error
  };

  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

  function toast(msg) {
    var el = $("#toast");
    el.textContent = "Simulated: " + msg;
    el.classList.add("show");
    clearTimeout(toast._t);
    toast._t = setTimeout(function () { el.classList.remove("show"); }, 2800);
  }

  function setConnection(mode) {
    state.connection = mode;
    var pill = $("#connPill");
    pill.className = "state-pill";
    if (mode === "connected") { pill.textContent = "Connected (mock)"; }
    else if (mode === "disconnected") { pill.textContent = "Disconnected"; pill.classList.add("err"); }
    else if (mode === "loading") { pill.textContent = "Reconnecting…"; pill.classList.add("load"); }
    else { pill.textContent = "Error: gateway unreachable"; pill.classList.add("err"); }
    $("#chatEmpty").classList.toggle("hidden", mode === "connected" && state.profileSelected);
    $("#chatError").classList.toggle("hidden", mode !== "error");
    $("#chatLoading").classList.toggle("hidden", mode !== "loading");
    $("#messages").classList.toggle("hidden", mode !== "connected" || !state.profileSelected);
  }

  function updateIdentity() {
    var host = state.connection === "connected" ? "mock-gateway:9119" : "—";
    var path = state.folderGranted ? "C:\\Repos\\DemoRepo" : "(no folder grant)";
    var branch = state.folderGranted ? "main" : "—";
    var profile = state.profileSelected ? "work" : "(none selected)";
    $("#identity").textContent = profile + " · " + host + " · " + path + " · " + branch;
  }

  function showView(name) {
    state.view = name;
    $$(".content").forEach(function (el) {
      el.classList.toggle("active", el.dataset.view === name);
    });
    $$(".nav [data-nav]").forEach(function (btn) {
      btn.setAttribute("aria-current", btn.dataset.nav === name ? "page" : "false");
    });
    if (name === "project" && state.folderGranted) {
      state.panelOpen = true;
      renderPanel();
    }
    if (name === "welcome" || name === "settings" || name === "bots" || name === "inbox") {
      state.panelOpen = false;
      renderPanel();
    }
  }

  function renderAdvanced() {
    $$(".advanced-only").forEach(function (el) {
      el.classList.toggle("hidden-by-simple", !state.advanced);
    });
    $("#advancedToggle").checked = state.advanced;
    $("#advancedToggleSettings").checked = state.advanced;
    $("#modeLabel").textContent = state.advanced ? "Advanced" : "Simple";
  }

  function renderPanel() {
    var host = $("#panelHost");
    host.classList.toggle("open", state.panelOpen && state.folderGranted);
    $$(".panel-pane").forEach(function (p) {
      p.classList.toggle("hidden", p.dataset.pane !== state.panelTab);
    });
    $$(".panel-tabs button").forEach(function (b) {
      b.setAttribute("aria-selected", b.dataset.tab === state.panelTab ? "true" : "false");
    });
  }

  function renderNeedsYou() {
    var b = $("#needsBadge");
    if (state.needsYou > 0 && state.profileSelected) {
      b.textContent = String(state.needsYou);
      b.classList.remove("hidden");
    } else {
      b.classList.add("hidden");
    }
  }

  function renderProfileGate() {
    $("#profileHidden").classList.toggle("hidden", state.profileSelected);
    $("#mainAppChrome").classList.toggle("dimmed", !state.profileSelected);
    renderNeedsYou();
    updateIdentity();
  }

  function openModal(id) {
    $("#" + id).classList.add("open");
    var focusable = $("#" + id).querySelector("button, [href], input");
    if (focusable) focusable.focus();
  }
  function closeModals() {
    $$(".modal-backdrop").forEach(function (m) { m.classList.remove("open"); });
  }

  function grantFolder() {
    state.folderGranted = true;
    updateIdentity();
    state.panelOpen = true;
    state.panelTab = "files";
    renderPanel();
    showView("project");
    toast("Folder grant recorded (not a real ACL)");
    closeModals();
  }

  function setTerminalOpen(open) {
    if (open && !state.terminalOptIn) {
      openModal("modalTerminal");
      return;
    }
    state.terminalOpen = open;
    $("#terminalDrawer").classList.toggle("open", open);
  }

  // Events
  $$(".nav [data-nav]").forEach(function (btn) {
    btn.addEventListener("click", function () { showView(btn.dataset.nav); });
  });

  $("#advancedToggle").addEventListener("change", function () {
    state.advanced = this.checked;
    renderAdvanced();
    toast("Advanced " + (state.advanced ? "on" : "off") + " — permissions unchanged");
  });
  $("#advancedToggleSettings").addEventListener("change", function () {
    state.advanced = this.checked;
    $("#advancedToggle").checked = state.advanced;
    renderAdvanced();
    toast("Advanced " + (state.advanced ? "on" : "off") + " — permissions unchanged");
  });

  $("#btnNeedsYou").addEventListener("click", function () {
    if (state.needsYou > 0) openModal("modalNeedsYou");
    else toast("No items waiting");
  });

  $("#btnGrantFolder").addEventListener("click", function () { openModal("modalGrant"); });
  $("#confirmGrant").addEventListener("click", grantFolder);
  $("#btnRevokeFolder").addEventListener("click", function () {
    state.folderGranted = false;
    state.panelOpen = false;
    state.terminalOpen = false;
    $("#terminalDrawer").classList.remove("open");
    updateIdentity();
    renderPanel();
    toast("Folder grant revoked (simulated)");
  });

  $$(".panel-tabs button").forEach(function (b) {
    b.addEventListener("click", function () {
      state.panelTab = b.dataset.tab;
      renderPanel();
    });
  });

  $("#btnOpenPanel").addEventListener("click", function () {
    if (!state.folderGranted) { openModal("modalGrant"); return; }
    state.panelOpen = !state.panelOpen;
    renderPanel();
  });

  $("#btnTerminal").addEventListener("click", function () {
    setTerminalOpen(!state.terminalOpen);
  });
  $("#confirmTerminal").addEventListener("click", function () {
    state.terminalOptIn = true;
    closeModals();
    setTerminalOpen(true);
    toast("Terminal opt-in saved (no real shell)");
  });
  $("#closeTerminal").addEventListener("click", function () {
    state.terminalOpen = false;
    $("#terminalDrawer").classList.remove("open");
  });

  $("#btnCheckpoint").addEventListener("click", function () {
    openModal("modalCheckpoint");
  });
  $("#confirmCheckpoint").addEventListener("click", function () {
    closeModals();
    toast("Checkpoint saved locally — not pushed");
  });

  $("#btnPush").addEventListener("click", function () {
    openModal("modalPush");
  });
  $("#confirmPush").addEventListener("click", function () {
    closeModals();
    toast("Push acknowledged — no network call made");
  });

  $("#btnWorktree").addEventListener("click", function () {
    openModal("modalWorktree");
  });
  $("#confirmWorktree").addEventListener("click", function () {
    closeModals();
    toast("Separate working copy created (label only)");
  });

  $("#btnSecretWarn").addEventListener("click", function () {
    openModal("modalSecret");
  });

  $("#approveNeeds").addEventListener("click", function () {
    state.needsYou = 0;
    renderNeedsYou();
    closeModals();
    toast("Approval sent (fake)");
  });

  $("#btnHideProfile").addEventListener("click", function () {
    state.profileSelected = false;
    renderProfileGate();
    toast("Profile deselected — data hidden");
  });
  $("#btnSelectProfile").addEventListener("click", function () {
    state.profileSelected = true;
    renderProfileGate();
    toast("Profile selected");
  });

  $$(".modal-backdrop").forEach(function (m) {
    m.addEventListener("click", function (e) {
      if (e.target === m) closeModals();
    });
  });
  $$("[data-close]").forEach(function (b) {
    b.addEventListener("click", closeModals);
  });

  // Scenarios
  var scenarios = {
    grant: function () { showView("project"); openModal("modalGrant"); },
    diff: function () {
      if (!state.folderGranted) grantFolder();
      showView("project");
      state.panelOpen = true;
      state.panelTab = "changes";
      renderPanel();
      toast("Showing synthetic diff");
    },
    checkpoint: function () { scenarios.diff(); openModal("modalCheckpoint"); },
    push: function () { scenarios.diff(); openModal("modalPush"); },
    worktree: function () { if (!state.folderGranted) grantFolder(); openModal("modalWorktree"); },
    secret: function () { openModal("modalSecret"); },
    needsyou: function () { state.needsYou = 1; renderNeedsYou(); openModal("modalNeedsYou"); },
    disconnected: function () { setConnection("disconnected"); showView("chat"); },
    loading: function () { setConnection("loading"); showView("chat"); },
    empty: function () { setConnection("connected"); showView("inbox"); },
    error: function () { setConnection("error"); showView("chat"); },
    profilehide: function () { state.profileSelected = false; renderProfileGate(); showView("chat"); },
    reconnect: function () { setConnection("connected"); state.profileSelected = true; renderProfileGate(); showView("chat"); },
  };
  $$("[data-scenario]").forEach(function (btn) {
    btn.addEventListener("click", function () {
      var fn = scenarios[btn.dataset.scenario];
      if (fn) fn();
    });
  });

  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape") closeModals();
    if (e.key === "`" && e.ctrlKey) {
      e.preventDefault();
      setTerminalOpen(!state.terminalOpen);
    }
  });

  // Composer fake send
  $("#composerForm").addEventListener("submit", function (e) {
    e.preventDefault();
    var input = $("#composerInput");
    if (!input.value.trim()) return;
    toast("Message not sent — synthetic UI");
    input.value = "";
  });

  // Init
  renderAdvanced();
  renderPanel();
  renderNeedsYou();
  renderProfileGate();
  setConnection("connected");
  updateIdentity();
  showView("welcome");
})();
