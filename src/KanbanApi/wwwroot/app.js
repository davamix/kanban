"use strict";

// Shared helpers (getCookie, csrfHeaders, guardAuth, escapeHtml, initials, date + toast + dialog
// utilities) live in common.js, loaded before this script.

// A stable, arbitrary icon per project so cards read distinctly (purely cosmetic).
const CARD_ICONS = ["language", "smartphone", "campaign", "analytics", "rocket_launch", "dataset", "design_services", "build"];
function iconFor(id) {
  let h = 0;
  for (const ch of id) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return CARD_ICONS[h % CARD_ICONS.length];
}

// --- API -----------------------------------------------------------------
// apiMe() lives in common.js (shared with the board screen).
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
// Update a project; returns the raw Response so the caller can branch on 200 / 400 / 403 / 404.
async function apiUpdateProject(id, payload) {
  return guardAuth(await fetch(`/api/projects/${encodeURIComponent(id)}`, {
    method: "PUT",
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

// Maps a project's Calendar-mirror status to a small card indicator (icon + tooltip) and a spoken
// fragment appended to the card's aria-label. Unknown/Skipped → nothing.
function mirrorBadge(status) {
  if (status === "Failed") {
    return {
      html: `<span class="mirror-status failed material-symbols-outlined" title="Calendar sync failed" aria-hidden="true">sync_problem</span>`,
      spoken: " Calendar sync failed.",
    };
  }
  if (status === "Mirrored") {
    return {
      html: `<span class="mirror-status synced material-symbols-outlined" title="Synced to Calendar" aria-hidden="true">event_available</span>`,
      spoken: " Synced to Calendar.",
    };
  }
  return { html: "", spoken: "" };
}

function cardHtml(p) {
  const badge = p.isOwner
    ? `<span class="badge owner">Owner</span>`
    : `<span class="badge shared">Shared</span>`;
  // Calendar-mirror hint — owner-only (they created the project; the mirror runs on create). Failed
  // is the one that matters (the Calendar copy is missing); Mirrored is a subtle "synced" cue;
  // Skipped (feature off / not attempted) shows nothing. See ADR 0009 / ecosystem-integration.md §6.
  const mirror = p.isOwner ? mirrorBadge(p.mirrorStatus) : { html: "", spoken: "" };
  // A spoken summary for screen readers (the card's visual bits are decorative on their own).
  const ariaLabel = escapeHtml(
    `Project: ${p.name}. ${p.isOwner ? "Owned by you" : "Shared with you"}. ${fmtRange(p.startDate, p.endDate)}.${mirror.spoken}`);
  // Only the owner can edit or delete a project, so only owner cards are draggable and carry those
  // affordances. Card interactions (all mirrored for keyboard + assistive tech, advertised via
  // aria-keyshortcuts): Enter/click opens the project's task board (next screen); Space opens the
  // edit form; Delete opens the delete-confirm dialog (mirroring a drag onto the delete zone).
  // The per-card Delete button is screen-reader-only until focused; the Edit affordance lives in a
  // panel that reveals below the card on hover/focus. See ADR 0006 (project editing).
  const deleteBtn = p.isOwner
    ? `<button type="button" class="card-delete" data-delete aria-label="Delete project ${escapeHtml(p.name)}">Delete</button>`
    : "";
  // Hover/focus action panel — owner-only, holds the Edit button (Space is the keyboard equivalent).
  const editPanel = p.isOwner
    ? `<div class="card-actions">
         <button type="button" class="card-edit" data-edit aria-label="Edit project ${escapeHtml(p.name)}">
           <span class="material-symbols-outlined" aria-hidden="true">edit</span>Edit
         </button>
       </div>`
    : "";
  const shortcuts = p.isOwner ? "Enter Space Delete" : "Enter";
  return `
    <article class="project-card" tabindex="0" draggable="${p.isOwner ? "true" : "false"}" aria-keyshortcuts="${shortcuts}"
             aria-label="${ariaLabel}" data-id="${escapeHtml(p.id)}" data-name="${escapeHtml(p.name.toLowerCase())}">
      <div class="card-top">
        <div class="card-id">
          <span class="card-icon material-symbols-outlined" style="font-variation-settings:'FILL' 1;">${iconFor(p.id)}</span>
          <h3 class="card-title" title="${escapeHtml(p.name)}">${escapeHtml(p.name)}</h3>
        </div>
        <div class="card-badges">${mirror.html}${badge}</div>
      </div>
      <p class="card-desc">${escapeHtml(p.description || "No description.")}</p>
      <div class="card-foot">
        ${avatarStack(p.assignees)}
        <span class="card-dates"><span class="material-symbols-outlined">event</span>${escapeHtml(fmtRange(p.startDate, p.endDate))}</span>
      </div>
      ${deleteBtn}
      ${editPanel}
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

// --- Project form modal (create + edit) ----------------------------------
// One modal drives both creating a project and editing an existing one. `mode` switches the title,
// submit label, seeded assignees, and the write call; `editId` holds the project being edited.
const formState = { users: [], selected: [], loaded: false, mode: "create", editId: null };
let lastFocused = null; // element to restore focus to when the modal closes

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
  const chosen = new Set(formState.selected.map((u) => u.id));
  const available = formState.users.filter((u) => !chosen.has(u.id));
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
  for (const u of formState.selected) {
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
        formState.selected = formState.selected.filter((s) => s.id !== u.id);
        renderChips();
        populateAssigneeSelect();
      });
      chip.appendChild(remove);
    }
    wrap.appendChild(chip);
  }
}

async function ensureUsersLoaded() {
  if (formState.loaded) return;
  try {
    formState.users = await apiUsers();
  } catch {
    formState.users = [];
  }
  formState.loaded = true;

  // /api/me only carries the session claims, which may lack a name/username — so the seeded owner
  // chip can fall back to the raw id. The directory has the Logto username; prefer it when present.
  const dir = state.me && formState.users.find((u) => u.id === state.me.id);
  const me = state.me && formState.selected.find((s) => s.id === state.me.id);
  if (dir && me) {
    me.name = dir.name || me.name;
    me.email = dir.email || me.email;
    renderChips();
  }

  populateAssigneeSelect();
}

// applyProblem() lives in common.js (it calls this page's fieldError/showToast).

// Reset the form for the given mode. `project` null → create (seed the current user as fixed owner
// assignee); otherwise edit (prefill every field + the project's assignees). The owner is always me
// in edit mode (edit is owner-only), so their chip stays the fixed, non-removable "(You)" chip.
function resetProjectForm(project) {
  document.getElementById("createForm").reset();
  clearErrors();
  if (project) {
    document.getElementById("fName").value = project.name;
    document.getElementById("fDescription").value = project.description ?? "";
    document.getElementById("fBudget").value = project.budget ?? "";
    document.getElementById("fStart").value = project.startDate;   // "yyyy-MM-dd" ← DateOnly
    document.getElementById("fEnd").value = project.endDate;
    formState.selected = project.assignees.map((a) => ({ id: a.id, name: a.name }));
  } else {
    // Seed the current user as a fixed assignee — the creator is always an assignee (owner).
    formState.selected = state.me ? [meAsUser()] : [];
  }
  renderChips();
  populateAssigneeSelect();
}

// Open the modal in create mode (project omitted) or edit mode (the project to edit).
function openProjectModal(project = null) {
  lastFocused = document.activeElement;
  formState.mode = project ? "edit" : "create";
  formState.editId = project ? project.id : null;

  document.getElementById("createTitle").textContent = project ? "Edit Project" : "New Project";
  document.getElementById("createSubtitle").textContent = project ? "Update the project details" : "Initialize a new workflow";
  document.getElementById("createSubmit").textContent = project ? "Save Changes" : "Create Project";

  resetProjectForm(project);
  document.getElementById("createModal").classList.remove("hidden");
  ensureUsersLoaded();
  document.getElementById("fName").focus();
}
function closeProjectModal() {
  document.getElementById("createModal").classList.add("hidden");
  // Return focus to whatever opened the dialog (the "Create New" button, or a card's Edit button).
  if (lastFocused && typeof lastFocused.focus === "function") lastFocused.focus();
}

async function submitProjectForm(e) {
  e.preventDefault();
  clearErrors();
  const editing = formState.mode === "edit";

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
    assigneeIds: formState.selected.map((u) => u.id),
  };

  const submit = document.getElementById("createSubmit");
  submit.disabled = true;
  try {
    if (editing) {
      const res = await apiUpdateProject(formState.editId, payload);
      if (res.status === 200) {
        const project = await res.json();
        const i = state.projects.findIndex((p) => p.id === project.id);
        if (i !== -1) state.projects[i] = project;
        renderStats(state.projects);
        renderProjects();
        closeProjectModal();
        // The grid was re-rendered, so lastFocused is detached — land focus on the edited card.
        document.querySelector(`.project-card[data-id="${CSS.escape(project.id)}"]`)?.focus();
        showToast("Project updated.");
        return;
      }
      if (res.status === 400) { applyProblem(await res.json().catch(() => null), "Could not save changes."); return; }
      if (res.status === 403) { showToast("Only the project owner can edit it.", true); return; }
      if (res.status === 404) {
        // Gone since the grid was loaded — drop it locally so the view reflects reality.
        state.projects = state.projects.filter((p) => p.id !== formState.editId);
        renderStats(state.projects);
        renderProjects();
        closeProjectModal();
        showToast("Project no longer exists.", true);
        return;
      }
      showToast("Could not save changes.", true);
      return;
    }

    const res = await apiCreateProject(payload);
    if (res.status === 201) {
      const project = await res.json();
      state.projects.unshift(project);
      renderStats(state.projects);
      renderProjects();
      closeProjectModal();
      showToast("Project created.");
      return;
    }
    if (res.status === 400) { applyProblem(await res.json().catch(() => null), "Could not create project."); return; }
    showToast("Could not create project.", true);
  } catch (err) {
    console.error(err);
    showToast(editing ? "Could not save changes." : "Could not create project.", true);
  } finally {
    submit.disabled = false;
  }
}

function wireProjectModal() {
  const modal = document.getElementById("createModal");
  document.getElementById("createNew").addEventListener("click", () => openProjectModal());
  document.getElementById("createClose").addEventListener("click", closeProjectModal);
  document.getElementById("createCancel").addEventListener("click", closeProjectModal);
  wireModalChrome(modal, closeProjectModal);

  // Selecting from the dropdown moves that user into a chip.
  document.getElementById("fAssignee").addEventListener("change", (e) => {
    const id = e.target.value;
    if (!id) return;
    const user = formState.users.find((u) => u.id === id);
    if (user && !formState.selected.some((s) => s.id === id)) {
      formState.selected.push(user);
      renderChips();
    }
    populateAssigneeSelect();
  });

  document.getElementById("createForm").addEventListener("submit", submitProjectForm);
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
const DELETE_ZONE_LABEL = "Drop here to delete project";

function wireDragToDelete() {
  const grid = document.getElementById("projectGrid");
  const zone = document.getElementById("deleteZone");
  const label = document.getElementById("deleteZoneLabel");
  const status = document.getElementById("dragStatus");
  let dragId = null;
  let dragName = null;

  // Announce drag progress to screen readers via the polite live region.
  const announce = (msg) => { if (status) status.textContent = msg; };
  // Reset the zone to its dormant label + clear the announcement.
  const resetZone = () => { label.textContent = DELETE_ZONE_LABEL; announce(""); };

  grid.addEventListener("dragstart", (e) => {
    const card = e.target.closest(".project-card");
    if (!card || card.getAttribute("draggable") !== "true") return;
    dragId = card.dataset.id;
    dragName = state.projects.find((p) => p.id === dragId)?.name ?? "this project";
    e.dataTransfer.setData("text/plain", dragId);
    e.dataTransfer.effectAllowed = "move";
    card.classList.add("dragging");
    zone.classList.add("armed");   // grey → live delete colour
    // The zone now names what's being dragged (visual + spoken feedback).
    label.textContent = `Release “${dragName}” here to delete`;
    announce(`“${dragName}” grabbed. Drop it on the delete zone to remove it.`);
  });
  grid.addEventListener("dragend", (e) => {
    const card = e.target.closest(".project-card");
    if (card) card.classList.remove("dragging");
    zone.classList.remove("armed", "over");
    resetZone();
    dragId = null;
    dragName = null;
  });

  zone.addEventListener("dragover", (e) => {
    if (dragId === null) return;
    e.preventDefault();                     // required to allow a drop
    e.dataTransfer.dropEffect = "move";
    if (!zone.classList.contains("over")) {   // announce only on entry, not every tick
      zone.classList.add("over");
      announce(`Over delete zone. Release to delete “${dragName}”.`);
    }
  });
  zone.addEventListener("dragleave", (e) => {
    if (!zone.contains(e.relatedTarget)) zone.classList.remove("over");
  });
  zone.addEventListener("drop", (e) => {
    e.preventDefault();
    const id = e.dataTransfer.getData("text/plain") || dragId;
    zone.classList.remove("armed", "over");
    // The confirm dialog (and, on success, the toast) carries feedback from here on.
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

  // Enter/click on a card opens that project's task board.
  const openBoard = (id) => { window.location.assign(`board.html?project=${encodeURIComponent(id)}`); };
  const projectFor = (card) => state.projects.find((p) => p.id === card.dataset.id);

  // Card click routing: the Edit button (in the hover panel) opens the edit form; the Delete button
  // (click / screen-reader path) opens the confirm dialog; anywhere else on a card opens its board
  // (next screen). The button branches come first and stop here so the click doesn't fall through.
  const grid = document.getElementById("projectGrid");
  grid.addEventListener("click", (e) => {
    const edit = e.target.closest("[data-edit]");
    if (edit) {
      const card = edit.closest(".project-card");
      const project = card && projectFor(card);
      if (project) openProjectModal(project);
      return;
    }
    const del = e.target.closest("[data-delete]");
    if (del) {
      const card = del.closest(".project-card");
      if (card) openDeleteModal(card.dataset.id);
      return;
    }
    const card = e.target.closest(".project-card");
    if (card) openBoard(card.dataset.id);
  });

  // Keyboard path (only when the card itself — not an in-card button — holds focus), mirroring the
  // pointer affordances: Enter opens the board; Space opens the edit form (owner-only); Delete /
  // Backspace opens the delete-confirm dialog (owner-only). Space/Delete are prevented from their
  // default (page scroll / back-nav) once handled.
  grid.addEventListener("keydown", (e) => {
    const card = e.target.closest(".project-card");
    if (!card || card !== e.target) return;
    const isOwner = card.getAttribute("draggable") === "true";

    if (e.key === "Enter") {
      e.preventDefault();
      openBoard(card.dataset.id);
    } else if ((e.key === " " || e.key === "Spacebar") && isOwner) {
      e.preventDefault();
      const project = projectFor(card);
      if (project) openProjectModal(project);
    } else if ((e.key === "Delete" || e.key === "Backspace") && isOwner) {
      e.preventDefault();
      openDeleteModal(card.dataset.id);
    }
  });
}

// --- Init ----------------------------------------------------------------
async function init() {
  wireStaticHandlers();
  wireProjectModal();
  wireDeleteModal();
  wireDragToDelete();
  try {
    state.me = await apiMe();
    if (!state.me) { redirectToLogin(); return; }
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
