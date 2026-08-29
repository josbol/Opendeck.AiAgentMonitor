// Minimal property-inspector runtime: works with OpenDeck (and Elgato's connectElgatoStreamDeckSocket signature).
let ws = null, uuid = null, actionInfo = null, settings = {}, globalSettings = {};
window.connectElgatoStreamDeckSocket = function (port, inUuid, registerEvent, info, inActionInfo) {
  uuid = inUuid;
  try { actionInfo = JSON.parse(inActionInfo); settings = (actionInfo.payload && actionInfo.payload.settings) || {}; } catch (e) { actionInfo = {}; }
  ws = new WebSocket("ws://127.0.0.1:" + port);
  ws.onopen = () => {
    ws.send(JSON.stringify({ event: registerEvent, uuid }));
    ws.send(JSON.stringify({ event: "getGlobalSettings", context: uuid }));
    render();
  };
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    if (msg.event === "didReceiveSettings") { settings = msg.payload.settings || {}; render(); }
    if (msg.event === "didReceiveGlobalSettings") { globalSettings = msg.payload.settings || {}; render(); }
    if (msg.event === "sendToPropertyInspector" && msg.payload) {
      if (msg.payload.globalSettings) { globalSettings = Object.assign({}, msg.payload.globalSettings); }
      if (msg.payload.snapshot) { window.lastSnapshot = msg.payload.snapshot; }
      render();
    }
  };
};
function send(o) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(o)); }
function saveSettings(patch) {
  settings = Object.assign({}, settings, patch);
  send({ event: "setSettings", context: uuid, payload: settings });
}
function saveGlobal(patch) {
  globalSettings = Object.assign({}, globalSettings, patch);
  send({ event: "setGlobalSettings", context: uuid, payload: globalSettings });
  send({ event: "sendToPlugin", action: actionInfo && actionInfo.action, context: uuid, payload: { command: "setGlobalSettings", settings: globalSettings } });
}
function bind(id, key, opts) {
  const el = document.getElementById(id); if (!el) return;
  opts = opts || {};
  const store = opts.global ? globalSettings : settings;
  const v = store[key];
  if (el.type === "checkbox") el.checked = v === undefined ? !!opts.def : !!v;
  else el.value = v === undefined || v === null ? (opts.def === undefined ? "" : opts.def) : v;
  if (!el.dataset.bound) {
    el.dataset.bound = "1";
    el.addEventListener("change", () => {
      let val = el.type === "checkbox" ? el.checked : el.value;
      if (opts.num) val = Number(val);
      (opts.global ? saveGlobal : saveSettings)({ [key]: val });
    });
  }
}
function render() {
  if (typeof window.bindFields === "function") window.bindFields();
  // shared global section
  bind("g-usageRefreshSeconds", "usageRefreshSeconds", { global: true, num: true, def: 300 });
  bind("g-networkQuota", "networkQuota", { global: true, def: true });
  bind("g-codexIdleMinutes", "codexIdleMinutes", { global: true, num: true, def: 120 });
  bind("g-contextWindow", "contextWindow", { global: true, num: true, def: 0 });
  bind("g-monitorProfile", "monitorProfile", { global: true, def: "AI Agents" });
  bind("g-mainProfile", "mainProfile", { global: true, def: "Default" });
  bind("g-tickSeconds", "tickSeconds", { global: true, num: true, def: 30 });
  bind("g-hookPort", "hookPort", { global: true, num: true, def: 43117 });
  bind("g-approvalHoldSeconds", "approvalHoldSeconds", { global: true, num: true, def: 30 });
  bind("g-holdOnlyWhenUnfocused", "holdOnlyWhenUnfocused", { global: true, def: false });
  bind("g-approvalPopup", "approvalPopup", { global: true, def: "auto" });
  const status = document.getElementById("status");
  if (status && window.lastSnapshot) {
    const s = window.lastSnapshot;
    status.textContent = (s.agents || []).map(a => `${a.provider} ${a.name} — ${a.state}${a.detail ? " (" + a.detail + ")" : ""}`).join("\n") || "no agents";
  }
}
window.addEventListener("DOMContentLoaded", () => {
  const b = document.getElementById("refresh");
  if (b) b.addEventListener("click", () => send({ event: "sendToPlugin", action: actionInfo && actionInfo.action, context: uuid, payload: { command: "refresh" } }));
});
