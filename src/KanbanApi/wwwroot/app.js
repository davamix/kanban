"use strict";

// --- Helpers -------------------------------------------------------------
function getCookie(name) {
  return document.cookie.split("; ").find((c) => c.startsWith(name + "="))?.split("=")[1];
}
// CSRF header for cookie-authenticated mutations (server validates it; bearer callers skip it).
function csrfHeaders() {
  const token = getCookie("XSRF-TOKEN");
  return token ? { "X-CSRF-TOKEN": decodeURIComponent(token) } : {};
}
// When the API says we're unauthenticated, send the browser to the BFF login (→ Logto hosted page).
function guardAuth(res) {
  if (res.status === 401) {
    window.location.assign("/login");
    throw new Error("Not authenticated");
  }
  return res;
}
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
// Parse a "yyyy-MM-dd" DateOnly without timezone drift.
function parseDate(s) {
  const [y, m, d] = s.split("-").map(Number);
  return new Date(y, m - 1, d);
}
const MONTHS = ["Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"];
function fmtRange(start, end) {
  const a = parseDate(start), b = parseDate(end);
  const left = `${MONTHS[a.getMonth()]} ${a.getDate()}`;
  const right = `${MONTHS[b.getMonth()]} ${b.getDate()}`;
  return `${left} – ${right}`;
}
// A stable, arbitrary icon per project so cards read distinctly (purely cosmetic).
const CARD_ICONS = ["language", "smartphone", "campaign", "analytics", "rocket_launch", "dataset", "design_services", "build"];
function iconFor(id) {
  let h = 0;
  for (const ch of id) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return CARD_ICONS[h % CARD_ICONS.length];
}

let toastTimer;
function showToast(message, isError = false) {
  const el = document.getElementById("toast");
  el.textContent = message;
  el.classList.toggle("error", isError);
  el.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.add("hidden"), 3000);
}

// --- API -----------------------------------------------------------------
async function apiMe() {
  const res = await fetch("/api/me");
  if (res.status === 401) return null;
  if (!res.ok) throw new Error("Failed to load current user");
  return res.json();
}
async function apiProjects() {
  const res = guardAuth(await fetch("/api/projects/"));
  if (!res.ok) throw new Error("Failed to load projects");
  return res.json();
}

// --- State ---------------------------------------------------------------
const state = { me: null, projects: [], filter: "" };

// --- Render --------------------------------------------------------------
function renderHeaderUser(me) {
  const label = me.displayName || me.email || "Account";
  document.getElementById("userName").textContent = label;
  document.getElementById("userAvatar").textContent = initials(me.displayName, me.email || me.id);
}

function renderStats(projects) {
  document.getElementById("statTotal").textContent = projects.length;
  document.getElementById("statOwned").textContent = projects.filter((p) => p.isOwner).length;
}

function avatarStack(assignees) {
  const shown = assignees.slice(0, 3);
  const rest = assignees.length - shown.length;
  const chips = shown.map((a) =>
    `<span class="avatar" title="${escapeHtml(a.name || a.id)}">${escapeHtml(initials(a.name, a.id))}</span>`);
  if (rest > 0) chips.push(`<span class="avatar more" title="${rest} more">+${rest}</span>`);
  return `<div class="avatar-stack">${chips.join("")}</div>`;
}

function cardHtml(p) {
  const badge = p.isOwner
    ? `<span class="badge owner">Owner</span>`
    : `<span class="badge shared">Shared</span>`;
  return `
    <article class="project-card" tabindex="0" data-id="${escapeHtml(p.id)}" data-name="${escapeHtml(p.name.toLowerCase())}">
      <div class="card-top">
        <div class="card-id">
          <span class="card-icon material-symbols-outlined" style="font-variation-settings:'FILL' 1;">${iconFor(p.id)}</span>
          <h3 class="card-title" title="${escapeHtml(p.name)}">${escapeHtml(p.name)}</h3>
        </div>
        ${badge}
      </div>
      <p class="card-desc">${escapeHtml(p.description || "No description.")}</p>
      <div class="card-foot">
        ${avatarStack(p.assignees)}
        <span class="card-dates"><span class="material-symbols-outlined">event</span>${escapeHtml(fmtRange(p.startDate, p.endDate))}</span>
      </div>
    </article>`;
}

function renderProjects() {
  const grid = document.getElementById("projectGrid");
  const empty = document.getElementById("emptyState");
  const noMatch = document.getElementById("noMatches");

  const all = state.projects;
  const filtered = state.filter
    ? all.filter((p) => p.name.toLowerCase().includes(state.filter))
    : all;

  empty.classList.toggle("hidden", all.length !== 0);
  noMatch.classList.toggle("hidden", !(all.length !== 0 && filtered.length === 0));
  grid.innerHTML = filtered.map(cardHtml).join("");
}

// --- Wiring --------------------------------------------------------------
function wireStaticHandlers() {
  // Fill every antiforgery field from the readable XSRF-TOKEN cookie so the logout POSTs validate.
  const token = decodeURIComponent(getCookie("XSRF-TOKEN") || "");
  document.getElementById("csrfField").value = token;
  document.querySelectorAll(".csrf-field").forEach((f) => (f.value = token));

  // Not-yet-built areas advertise themselves rather than dead-ending.
  document.querySelectorAll("[data-soon]").forEach((el) =>
    el.addEventListener("click", (e) => {
      e.preventDefault();
      showToast(`${el.dataset.soon} — coming soon`);
    }));

  // Client-side name filter.
  const search = document.getElementById("searchInput");
  document.getElementById("searchForm").addEventListener("submit", (e) => e.preventDefault());
  search.addEventListener("input", () => {
    state.filter = search.value.trim().toLowerCase();
    renderProjects();
  });

  // Opening a project board is the next screen.
  document.getElementById("projectGrid").addEventListener("click", (e) => {
    const card = e.target.closest(".project-card");
    if (card) showToast("Project board — coming soon");
  });
}

// --- Init ----------------------------------------------------------------
async function init() {
  wireStaticHandlers();
  try {
    state.me = await apiMe();
    if (!state.me) { window.location.assign("/login"); return; }
    renderHeaderUser(state.me);

    state.projects = await apiProjects();
    renderStats(state.projects);
    renderProjects();
  } catch (err) {
    console.error(err);
    showToast("Something went wrong loading your projects.", true);
  }
}

document.addEventListener("DOMContentLoaded", init);
