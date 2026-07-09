"use strict";

// Helpers shared by the project-selection screen (app.js) and the board (board.js). Loaded as a
// plain script before them, so these stay global — no module system, in keeping with the lean,
// build-free frontend.

// --- Cookies / CSRF / auth ----------------------------------------------
function getCookie(name) {
  return document.cookie.split("; ").find((c) => c.startsWith(name + "="))?.split("=")[1];
}
// CSRF header for cookie-authenticated mutations (server validates it; bearer callers skip it).
function csrfHeaders() {
  const token = getCookie("XSRF-TOKEN");
  return token ? { "X-CSRF-TOKEN": decodeURIComponent(token) } : {};
}
// Send the browser to BFF sign-in, preserving the current location so silent re-auth (an active
// Logto session) returns the user to where they were rather than the default landing page.
function redirectToLogin() {
  const returnUrl = location.pathname + location.search;
  window.location.assign(`/login?returnUrl=${encodeURIComponent(returnUrl)}`);
}
// When the API says we're unauthenticated, send the browser to the BFF login (→ Logto hosted page).
function guardAuth(res) {
  if (res.status === 401) {
    redirectToLogin();
    throw new Error("Not authenticated");
  }
  return res;
}

// --- Text ----------------------------------------------------------------
function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
function initials(name, id) {
  const src = (name || id || "?").trim();
  const parts = src.split(/\s+/).filter(Boolean);
  if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
  return src.slice(0, 2).toUpperCase();
}

// --- Dates ---------------------------------------------------------------
// Parse a "yyyy-MM-dd" DateOnly without timezone drift.
function parseDate(s) {
  const [y, m, d] = s.split("-").map(Number);
  return new Date(y, m - 1, d);
}
const MONTHS = ["Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"];
function fmtDate(s) {
  const d = parseDate(s);
  return `${MONTHS[d.getMonth()]} ${d.getDate()}`;
}
function fmtRange(start, end) {
  return `${fmtDate(start)} – ${fmtDate(end)}`;
}

// --- Toast ---------------------------------------------------------------
let toastTimer;
function showToast(message, isError = false) {
  const el = document.getElementById("toast");
  if (!el) return;
  el.textContent = message;
  el.classList.toggle("error", isError);
  el.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.add("hidden"), 3000);
}

// --- API / problem details ----------------------------------------------
// The signed-in user (BFF session claims), or null when unauthenticated.
async function apiMe() {
  const res = await fetch("/api/me");
  if (res.status === 401) return null;
  if (!res.ok) throw new Error("Failed to load current user");
  return res.json();
}
// Copy RFC 9457 field errors ({ errors: { field: [msg, …] } }) onto the form inputs via the page's
// own fieldError(name, message), or fall back to a toast. Keys are camelCase [name]s.
function applyProblem(problem, fallback) {
  const errors = problem?.errors;
  if (errors) {
    for (const [key, msgs] of Object.entries(errors))
      fieldError(key.charAt(0).toLowerCase() + key.slice(1), Array.isArray(msgs) ? msgs[0] : String(msgs));
  } else {
    showToast(problem?.title || fallback, true);
  }
}

// --- Dialogs -------------------------------------------------------------
// Visible, focusable elements inside a container (for the dialog focus trap).
function focusablesIn(container) {
  const sel = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
  return [...container.querySelectorAll(sel)].filter((el) => el.offsetParent !== null);
}
// Shared dialog chrome for every modal: backdrop-click + Escape dismiss and a Tab focus trap (ARIA
// dialog requirement). Kept in one place so new dialogs reuse it instead of re-copying the plumbing.
function wireModalChrome(modal, close) {
  modal.addEventListener("click", (e) => { if (e.target === modal) close(); });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && !modal.classList.contains("hidden")) close();
  });
  modal.addEventListener("keydown", (e) => {
    if (e.key !== "Tab") return;
    const f = focusablesIn(modal);
    if (f.length === 0) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  });
}
