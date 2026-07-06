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
// The assignee directory (all Logto users). Empty when Logto's Management API isn't configured.
async function apiUsers() {
  const res = guardAuth(await fetch("/api/users"));
  if (!res.ok) throw new Error("Failed to load users");
  return res.json();
}
// Create a project; returns the raw Response so the caller can branch on 201 vs 400 (validation).
async function apiCreateProject(payload) {
  return guardAuth(await fetch("/api/projects/", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...csrfHeaders() },
    body: JSON.stringify(payload),
  }));
}
// Delete a project; returns the raw Response so the caller can branch on 204 / 403 / 404.
async function apiDeleteProject(id) {
  return guardAuth(await fetch(`/api/projects/${encodeURIComponent(id)}`, {
    method: "DELETE",
    headers: { ...csrfHeaders() },
  }));
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
  // A spoken summary for screen readers (the card's visual bits are decorative on their own).
  const ariaLabel = escapeHtml(
    `Project: ${p.name}. ${p.isOwner ? "Owned by you" : "Shared with you"}. ${fmtRange(p.startDate, p.endDate)}.`);
  // Only the owner can delete a project, so only owner cards are draggable and carry the delete
  // affordance. Two keyboard/assistive-tech paths to the same confirm dialog (mirroring dragging the
  // card onto the delete zone): pressing Delete while the card is focused (advertised via
  // aria-keyshortcuts), and a per-card Delete button that is screen-reader-only until focused, then
  // revealed at the card's top-right. Enter on the card is reserved for opening the project (next
  // screen). See ADR 0005 (accessible drag-and-drop follow-up).
  const deleteBtn = p.isOwner
    ? `<button type="button" class="card-delete" data-delete aria-label="Delete project ${escapeHtml(p.name)}">Delete</button>`
    : "";
  const deleteShortcut = p.isOwner ? ` aria-keyshortcuts="Delete"` : "";
  return `
    <article class="project-card" tabindex="0" draggable="${p.isOwner ? "true" : "false"}"${deleteShortcut}
             aria-label="${ariaLabel}" data-id="${escapeHtml(p.id)}" data-name="${escapeHtml(p.name.toLowerCase())}">
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
      ${deleteBtn}
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

// --- Create-project modal ------------------------------------------------
const createState = { users: [], selected: [], loaded: false };
let lastFocused = null; // element to restore focus to when the modal closes

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

function userLabel(u) {
  return u.name || u.email || u.id;
}
// The signed-in user, shaped like a directory user, so they can seed the assignee list.
function meAsUser() {
  return { id: state.me.id, name: state.me.displayName || state.me.email, email: state.me.email };
}

function fieldError(name, message) {
  const el = document.querySelector(`.field-error[data-error="${name}"]`);
  if (el) el.textContent = message || "";
  const input = document.querySelector(`#createForm [name="${name}"]`);
  if (input) input.setAttribute("aria-invalid", message ? "true" : "false");
}
function clearErrors() {
  document.querySelectorAll("#createForm .field-error").forEach((el) => (el.textContent = ""));
  document.querySelectorAll("#createForm [aria-invalid]").forEach((el) => el.setAttribute("aria-invalid", "false"));
}

function populateAssigneeSelect() {
  const select = document.getElementById("fAssignee");
  const chosen = new Set(createState.selected.map((u) => u.id));
  const available = createState.users.filter((u) => !chosen.has(u.id));
  select.innerHTML = `<option value="">Add assignee…</option>`;
  for (const u of available) {
    const opt = document.createElement("option");
    opt.value = u.id;
    opt.textContent = userLabel(u);
    select.appendChild(opt);
  }
  select.disabled = available.length === 0;
  document.getElementById("assigneeHint").classList.toggle("hidden", available.length !== 0);
}

function renderChips() {
  const wrap = document.getElementById("assigneeChips");
  wrap.innerHTML = "";
  for (const u of createState.selected) {
    const isMe = u.id === state.me?.id;
    const chip = document.createElement("span");
    chip.className = isMe ? "chip owner" : "chip";
    const label = document.createElement("span");
    // The creator is always an assignee (owner), so their chip is fixed and not removable.
    label.textContent = isMe ? `${userLabel(u)} (You)` : userLabel(u);
    chip.appendChild(label);
    if (!isMe) {
      const remove = document.createElement("button");
      remove.type = "button";
      remove.setAttribute("aria-label", `Remove ${userLabel(u)}`);
      remove.innerHTML = `<span class="material-symbols-outlined">close</span>`;
      remove.addEventListener("click", () => {
        createState.selected = createState.selected.filter((s) => s.id !== u.id);
        renderChips();
        populateAssigneeSelect();
      });
      chip.appendChild(remove);
    }
    wrap.appendChild(chip);
  }
}

async function ensureUsersLoaded() {
  if (createState.loaded) return;
  try {
    createState.users = await apiUsers();
  } catch {
    createState.users = [];
  }
  createState.loaded = true;

  // /api/me only carries the session claims, which may lack a name/username — so the seeded owner
  // chip can fall back to the raw id. The directory has the Logto username; prefer it when present.
  const dir = state.me && createState.users.find((u) => u.id === state.me.id);
  const me = state.me && createState.selected.find((s) => s.id === state.me.id);
  if (dir && me) {
    me.name = dir.name || me.name;
    me.email = dir.email || me.email;
    renderChips();
  }

  populateAssigneeSelect();
}

function resetCreateForm() {
  document.getElementById("createForm").reset();
  // Seed the current user as a fixed assignee — the creator is always an assignee (owner).
  createState.selected = state.me ? [meAsUser()] : [];
  clearErrors();
  renderChips();
  populateAssigneeSelect();
}

function openCreateModal() {
  lastFocused = document.activeElement;
  resetCreateForm();
  document.getElementById("createModal").classList.remove("hidden");
  ensureUsersLoaded();
  document.getElementById("fName").focus();
}
function closeCreateModal() {
  document.getElementById("createModal").classList.add("hidden");
  // Return focus to whatever opened the dialog (usually the "Create New" button).
  if (lastFocused && typeof lastFocused.focus === "function") lastFocused.focus();
}

async function submitCreate(e) {
  e.preventDefault();
  clearErrors();

  const name = document.getElementById("fName").value.trim();
  const description = document.getElementById("fDescription").value.trim();
  const budgetRaw = document.getElementById("fBudget").value;
  const startDate = document.getElementById("fStart").value;
  const endDate = document.getElementById("fEnd").value;

  // Client-side checks mirror the server so users get inline feedback before the round-trip.
  let ok = true;
  if (!name) { fieldError("name", "Project name is required."); ok = false; }
  if (!startDate) { fieldError("startDate", "Start date is required."); ok = false; }
  if (!endDate) { fieldError("endDate", "End date is required."); ok = false; }
  else if (startDate && endDate < startDate) { fieldError("endDate", "End date must be on or after the start date."); ok = false; }
  if (budgetRaw !== "" && Number(budgetRaw) < 0) { fieldError("budget", "Budget cannot be negative."); ok = false; }
  if (!ok) return;

  const payload = {
    name,
    description: description || null,
    startDate,
    endDate,
    budget: budgetRaw === "" ? null : Number(budgetRaw),
    assigneeIds: createState.selected.map((u) => u.id),
  };

  const submit = document.getElementById("createSubmit");
  submit.disabled = true;
  try {
    const res = await apiCreateProject(payload);
    if (res.status === 201) {
      const project = await res.json();
      state.projects.unshift(project);
      renderStats(state.projects);
      renderProjects();
      closeCreateModal();
      showToast("Project created.");
      return;
    }
    if (res.status === 400) {
      const problem = await res.json().catch(() => null);
      const errors = problem?.errors;
      if (errors) {
        // RFC 9457 problem-details: { errors: { field: [msg, …] } }. Keys are camelCase field names.
        for (const [key, msgs] of Object.entries(errors))
          fieldError(key.charAt(0).toLowerCase() + key.slice(1), Array.isArray(msgs) ? msgs[0] : String(msgs));
      } else {
        showToast(problem?.title || "Could not create project.", true);
      }
      return;
    }
    showToast("Could not create project.", true);
  } catch (err) {
    console.error(err);
    showToast("Could not create project.", true);
  } finally {
    submit.disabled = false;
  }
}

function wireCreateModal() {
  const modal = document.getElementById("createModal");
  document.getElementById("createNew").addEventListener("click", openCreateModal);
  document.getElementById("createClose").addEventListener("click", closeCreateModal);
  document.getElementById("createCancel").addEventListener("click", closeCreateModal);
  wireModalChrome(modal, closeCreateModal);

  // Selecting from the dropdown moves that user into a chip.
  document.getElementById("fAssignee").addEventListener("change", (e) => {
    const id = e.target.value;
    if (!id) return;
    const user = createState.users.find((u) => u.id === id);
    if (user && !createState.selected.some((s) => s.id === id)) {
      createState.selected.push(user);
      renderChips();
    }
    populateAssigneeSelect();
  });

  document.getElementById("createForm").addEventListener("submit", submitCreate);
}

// --- Drag-to-delete + confirm modal --------------------------------------
const deleteState = { project: null };

// Enable the Delete button only when the typed text exactly matches the project name.
function updateDeleteButton() {
  const input = document.getElementById("fConfirmName");
  const btn = document.getElementById("deleteSubmit");
  const match = !!deleteState.project && input.value.trim() === deleteState.project.name;
  btn.disabled = !match;
}

function openDeleteModal(id) {
  const project = state.projects.find((p) => p.id === id);
  if (!project) return;
  deleteState.project = project;
  lastFocused = document.activeElement;

  // Title/description are cosmetic (per the brief): name the project being deleted.
  document.getElementById("deleteProjectName").textContent = project.name;
  const input = document.getElementById("fConfirmName");
  input.value = "";
  input.setAttribute("placeholder", project.name);
  updateDeleteButton();

  document.getElementById("deleteModal").classList.remove("hidden");
  input.focus();
}
function closeDeleteModal() {
  document.getElementById("deleteModal").classList.add("hidden");
  deleteState.project = null;
  if (lastFocused && typeof lastFocused.focus === "function") lastFocused.focus();
}

async function submitDelete(e) {
  e.preventDefault();
  const project = deleteState.project;
  // Guard against submit while the name doesn't match (e.g. Enter key).
  if (!project || document.getElementById("fConfirmName").value.trim() !== project.name) return;

  const btn = document.getElementById("deleteSubmit");
  btn.disabled = true;
  try {
    const res = await apiDeleteProject(project.id);
    if (res.status === 204 || res.status === 404) {
      // 404 → already gone; drop it locally either way so the grid reflects reality.
      state.projects = state.projects.filter((p) => p.id !== project.id);
      renderStats(state.projects);
      renderProjects();
      closeDeleteModal();
      showToast(res.status === 204 ? "Project deleted." : "Project no longer exists.");
      return;
    }
    if (res.status === 403) {
      showToast("Only the project owner can delete it.", true);
    } else {
      showToast("Could not delete project.", true);
    }
  } catch (err) {
    console.error(err);
    showToast("Could not delete project.", true);
  } finally {
    updateDeleteButton();
  }
}

function wireDeleteModal() {
  const modal = document.getElementById("deleteModal");
  document.getElementById("deleteClose").addEventListener("click", closeDeleteModal);
  document.getElementById("deleteCancel").addEventListener("click", closeDeleteModal);
  document.getElementById("fConfirmName").addEventListener("input", updateDeleteButton);
  document.getElementById("deleteForm").addEventListener("submit", submitDelete);
  wireModalChrome(modal, closeDeleteModal);
}

// Native HTML5 drag-and-drop: dragging an (owner) card arms the delete zone; dropping opens the
// confirm dialog. The zone is greyed and pointer-events:none until armed (so it never blocks clicks).
function wireDragToDelete() {
  const grid = document.getElementById("projectGrid");
  const zone = document.getElementById("deleteZone");
  let dragId = null;

  grid.addEventListener("dragstart", (e) => {
    const card = e.target.closest(".project-card");
    if (!card || card.getAttribute("draggable") !== "true") return;
    dragId = card.dataset.id;
    e.dataTransfer.setData("text/plain", dragId);
    e.dataTransfer.effectAllowed = "move";
    card.classList.add("dragging");
    zone.classList.add("armed");   // grey → live delete colour
  });
  grid.addEventListener("dragend", (e) => {
    const card = e.target.closest(".project-card");
    if (card) card.classList.remove("dragging");
    zone.classList.remove("armed", "over");
    dragId = null;
  });

  zone.addEventListener("dragover", (e) => {
    if (dragId === null) return;
    e.preventDefault();                     // required to allow a drop
    e.dataTransfer.dropEffect = "move";
    zone.classList.add("over");
  });
  zone.addEventListener("dragleave", (e) => {
    if (!zone.contains(e.relatedTarget)) zone.classList.remove("over");
  });
  zone.addEventListener("drop", (e) => {
    e.preventDefault();
    const id = e.dataTransfer.getData("text/plain") || dragId;
    zone.classList.remove("armed", "over");
    if (id) openDeleteModal(id);
  });
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

  // The per-card Delete button (click / screen-reader path) opens the confirm dialog; anywhere else
  // on a card opens its board (the next screen). The delete branch comes first and stops here so the
  // click doesn't also fall through to the board.
  const grid = document.getElementById("projectGrid");
  grid.addEventListener("click", (e) => {
    const del = e.target.closest("[data-delete]");
    if (del) {
      const card = del.closest(".project-card");
      if (card) openDeleteModal(card.dataset.id);
      return;
    }
    const card = e.target.closest(".project-card");
    if (card) showToast("Project board — coming soon");
  });

  // Keyboard path: pressing Delete (or Backspace, the "delete" key on many keyboards) while an owner
  // card itself holds focus opens the confirm dialog. Enter is deliberately left free for opening the
  // project (a later screen). Only fires when the card — not the in-card Delete button — is focused.
  grid.addEventListener("keydown", (e) => {
    if (e.key !== "Delete" && e.key !== "Backspace") return;
    const card = e.target.closest(".project-card");
    if (!card || card !== e.target || card.getAttribute("draggable") !== "true") return;
    e.preventDefault();
    openDeleteModal(card.dataset.id);
  });
}

// --- Init ----------------------------------------------------------------
async function init() {
  wireStaticHandlers();
  wireCreateModal();
  wireDeleteModal();
  wireDragToDelete();
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
