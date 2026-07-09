"use strict";

// Task board screen. Reads the project id from the query string, renders columns + tasks, and wires
// the task form, task drag-to-move, and (owner-only) column add/rename/reorder/delete. Shared helpers
// (getCookie, csrfHeaders, guardAuth, escapeHtml, initials, fmtDate, showToast, wireModalChrome …)
// come from common.js. Every mutation re-fetches the board and re-renders, so the client never has to
// reconcile positions by hand — the server stays authoritative.

const projectId = new URLSearchParams(location.search).get("project");

const PRIORITIES = ["Low", "Medium", "High", "Urgent"];

// --- API -----------------------------------------------------------------
// apiMe() and applyProblem() live in common.js (shared with the project-selection screen).
const API = `/api/projects/${encodeURIComponent(projectId)}`;

async function apiBoard() {
  const res = guardAuth(await fetch(`${API}/board`));
  if (res.status === 404) return null;
  if (!res.ok) throw new Error("Failed to load board");
  return res.json();
}
const jsonHeaders = () => ({ "Content-Type": "application/json", ...csrfHeaders() });

async function apiCreateTask(payload) {
  return guardAuth(await fetch(`${API}/tasks`, { method: "POST", headers: jsonHeaders(), body: JSON.stringify(payload) }));
}
async function apiUpdateTask(id, payload) {
  return guardAuth(await fetch(`${API}/tasks/${encodeURIComponent(id)}`, { method: "PUT", headers: jsonHeaders(), body: JSON.stringify(payload) }));
}
async function apiMoveTask(id, payload) {
  return guardAuth(await fetch(`${API}/tasks/${encodeURIComponent(id)}/move`, { method: "PUT", headers: jsonHeaders(), body: JSON.stringify(payload) }));
}
async function apiDeleteTask(id) {
  return guardAuth(await fetch(`${API}/tasks/${encodeURIComponent(id)}`, { method: "DELETE", headers: { ...csrfHeaders() } }));
}
async function apiCreateColumn(name) {
  return guardAuth(await fetch(`${API}/columns`, { method: "POST", headers: jsonHeaders(), body: JSON.stringify({ name }) }));
}
async function apiRenameColumn(id, name) {
  return guardAuth(await fetch(`${API}/columns/${encodeURIComponent(id)}`, { method: "PUT", headers: jsonHeaders(), body: JSON.stringify({ name }) }));
}
async function apiReorderColumns(orderedIds) {
  return guardAuth(await fetch(`${API}/columns/order`, { method: "PUT", headers: jsonHeaders(), body: JSON.stringify({ orderedIds }) }));
}
async function apiDeleteColumn(id) {
  return guardAuth(await fetch(`${API}/columns/${encodeURIComponent(id)}`, { method: "DELETE", headers: { ...csrfHeaders() } }));
}

// --- State ---------------------------------------------------------------
const state = { me: null, board: null, filter: "" };
// Transient UI state kept in JS (not the DOM) so a re-render restores it deterministically.
const ui = { addingColumn: false, renamingColumnId: null };

// --- Small DOM helper ----------------------------------------------------
function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === "class") node.className = v;
    else if (k === "html") node.innerHTML = v;
    else if (k.startsWith("on") && typeof v === "function") node.addEventListener(k.slice(2), v);
    else if (k === "dataset") Object.assign(node.dataset, v);
    else if (v !== null && v !== undefined && v !== false) node.setAttribute(k, v);
  }
  for (const c of children.flat()) {
    if (c == null) continue;
    node.appendChild(typeof c === "string" ? document.createTextNode(c) : c);
  }
  return node;
}

// --- Render: header ------------------------------------------------------
function renderHeaderUser(me) {
  document.getElementById("userName").textContent = me.displayName || me.email || "Account";
  document.getElementById("userAvatar").textContent = initials(me.displayName, me.email || me.id);
}

function renderBoardHead() {
  const b = state.board;
  document.getElementById("boardTitle").textContent = b.projectName;
  const count = b.columns.reduce((n, c) => n + c.tasks.length, 0);
  const workflow = b.columns.map((c) => c.name).join(" → ");
  document.getElementById("boardSubtitle").textContent =
    `${count} task${count === 1 ? "" : "s"}${workflow ? " · " + workflow : ""}`;

  const stack = document.getElementById("boardAssignees");
  const shown = b.assignees.slice(0, 4);
  const rest = b.assignees.length - shown.length;
  stack.innerHTML = "";
  for (const a of shown)
    stack.appendChild(el("span", { class: "avatar", title: a.name || a.id }, initials(a.name, a.id)));
  if (rest > 0) stack.appendChild(el("span", { class: "avatar more", title: `${rest} more` }, `+${rest}`));

  document.getElementById("addTask").disabled = false;
}

// --- Render: board -------------------------------------------------------
function renderBoard() {
  const boardEl = document.getElementById("board");
  const empty = document.getElementById("boardEmpty");
  boardEl.innerHTML = "";

  const b = state.board;
  b.columns.forEach((col, idx) => boardEl.appendChild(renderColumn(col, idx)));
  if (b.isOwner) boardEl.appendChild(renderAddColumn());

  const totalTasks = b.columns.reduce((n, c) => n + c.tasks.length, 0);
  empty.classList.toggle("hidden", totalTasks !== 0 || b.columns.length !== 0);
}

function renderColumn(col, idx) {
  const isOwner = state.board.isOwner;
  const last = state.board.columns.length - 1;

  // Header: name + count, and (owner) reorder / rename / delete controls.
  let header;
  if (ui.renamingColumnId === col.id) {
    header = renderColumnRename(col);
  } else {
    const title = el("div", { class: "column-title-wrap" },
      el("span", { class: "column-name" }, col.name),
      el("span", { class: "column-count" }, String(col.tasks.length)));
    // A column can only be deleted once empty, so disable the control (with an explaining tooltip)
    // rather than letting the user confirm a delete that the server will then refuse.
    const hasTasks = col.tasks.length > 0;
    const actions = isOwner
      ? el("div", { class: "column-actions" },
          iconButton("chevron_left", "Move column left", () => moveColumn(idx, -1), idx === 0),
          iconButton("chevron_right", "Move column right", () => moveColumn(idx, 1), idx === last),
          iconButton("edit", `Rename ${col.name}`, () => { ui.renamingColumnId = col.id; renderBoard(); }),
          iconButton("delete", hasTasks ? `Delete ${col.name} — move or delete its tasks first` : `Delete ${col.name}`,
            () => confirmDeleteColumn(col), hasTasks))
      : null;
    header = el("header", { class: "column-head" }, title, actions);
  }

  // Task list — the drop target for task drags.
  const list = el("div", { class: "column-tasks", dataset: { column: col.id } });
  const tasks = state.filter
    ? col.tasks.filter((t) => t.title.toLowerCase().includes(state.filter))
    : col.tasks;
  for (const t of tasks) list.appendChild(renderTaskCard(t));
  wireColumnDropTarget(list);

  const addTask = el("button", { class: "column-add-task", type: "button",
    onclick: () => openTaskModal(null, col.id) },
    el("span", { class: "material-symbols-outlined" }, "add"), "Add task");

  return el("section", { class: "column", dataset: { id: col.id } }, header, list, addTask);
}

function renderColumnRename(col) {
  const input = el("input", { class: "column-rename-input", type: "text", maxlength: "100", value: col.name, "aria-label": "Column name" });
  const commit = async () => {
    const name = input.value.trim();
    if (!name || name === col.name) { ui.renamingColumnId = null; renderBoard(); return; }
    await withColumnResult(await apiRenameColumn(col.id, name), "Column renamed.", "Could not rename column.");
  };
  const cancel = () => { ui.renamingColumnId = null; renderBoard(); };
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); commit(); }
    else if (e.key === "Escape") { e.preventDefault(); cancel(); }
  });
  const form = el("div", { class: "column-head editing" }, input,
    iconButton("check", "Save name", commit),
    iconButton("close", "Cancel", cancel));
  // Focus after it's in the DOM.
  requestAnimationFrame(() => { input.focus(); input.select(); });
  return form;
}

function renderAddColumn() {
  if (!ui.addingColumn)
    return el("button", { class: "column-add", type: "button",
      onclick: () => { ui.addingColumn = true; renderBoard(); } },
      el("span", { class: "material-symbols-outlined" }, "add"), "Add column");

  const input = el("input", { class: "column-rename-input", type: "text", maxlength: "100", placeholder: "Column name…", "aria-label": "New column name" });
  const commit = async () => {
    const name = input.value.trim();
    if (!name) { ui.addingColumn = false; renderBoard(); return; }
    await withColumnResult(await apiCreateColumn(name), "Column added.", "Could not add column.");
  };
  const cancel = () => { ui.addingColumn = false; renderBoard(); };
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); commit(); }
    else if (e.key === "Escape") { e.preventDefault(); cancel(); }
  });
  requestAnimationFrame(() => input.focus());
  return el("div", { class: "column column-add editing" },
    el("div", { class: "column-head" }, input,
      iconButton("check", "Add column", commit),
      iconButton("close", "Cancel", cancel)));
}

function iconButton(icon, label, onClick, disabled = false) {
  return el("button", { class: "icon-btn sm", type: "button", "aria-label": label, title: label,
    disabled: disabled || false, onclick: onClick },
    el("span", { class: "material-symbols-outlined" }, icon));
}

function renderTaskCard(t) {
  const labels = (t.labels || []).slice(0, 4).map((l) =>
    `<span class="task-label">${escapeHtml(l)}</span>`).join("");
  const due = t.dueDate
    ? `<span class="task-due"><span class="material-symbols-outlined">event</span>${escapeHtml(fmtDate(t.dueDate))}</span>`
    : "";
  const assignee = t.assigneeId
    ? `<span class="avatar" title="${escapeHtml(t.assigneeName || t.assigneeId)}">${escapeHtml(initials(t.assigneeName, t.assigneeId))}</span>`
    : "";
  const prio = escapeHtml(t.priority || "Medium");
  const card = el("article", {
    class: "task-card", draggable: "true", tabindex: "0",
    dataset: { id: t.id, column: t.columnId },
    "aria-label": `Task: ${t.title}. Priority ${prio}.`,
    html: `
      ${labels ? `<div class="task-labels">${labels}</div>` : ""}
      <h4 class="task-title">${escapeHtml(t.title)}</h4>
      <div class="task-meta">
        <span class="pill priority-${prio.toLowerCase()}">${prio}</span>
        ${due}
        <span class="task-meta-spacer"></span>
        ${assignee}
      </div>`,
  });
  card.addEventListener("click", () => openTaskModal(t));
  card.addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); openTaskModal(t); }
  });
  wireTaskDrag(card);
  return card;
}

// --- Column ops ----------------------------------------------------------
async function moveColumn(idx, delta) {
  const cols = state.board.columns;
  const target = idx + delta;
  if (target < 0 || target >= cols.length) return;
  const order = cols.map((c) => c.id);
  [order[idx], order[target]] = [order[target], order[idx]];
  const res = await apiReorderColumns(order);
  if (res.ok) { await loadBoard(); } else { showToast("Could not reorder columns.", true); }
}

function confirmDeleteColumn(col) {
  openConfirm({
    title: "Delete column?",
    message: `Delete the “${col.name}” column? Its tasks must be moved or deleted first.`,
    confirmLabel: "Delete column",
    onConfirm: async () => {
      const res = await apiDeleteColumn(col.id);
      if (res.status === 204) { closeConfirm(); await loadBoard(); showToast("Column deleted."); return; }
      if (res.status === 409) { closeConfirm(); showToast("Move or delete this column's tasks first.", true); return; }
      if (res.status === 403) { closeConfirm(); showToast("Only the project owner can delete columns.", true); return; }
      closeConfirm(); showToast("Could not delete column.", true);
    },
  });
}

// Shared handling for column create/rename responses (201/204 ok, else problem).
async function withColumnResult(res, okMsg, failMsg) {
  if (res.status === 201 || res.status === 204) {
    ui.addingColumn = false; ui.renamingColumnId = null;
    await loadBoard();
    showToast(okMsg);
    return;
  }
  if (res.status === 403) { showToast("Only the project owner can manage columns.", true); }
  else if (res.status === 400) {
    const p = await res.json().catch(() => null);
    showToast(p?.errors?.name?.[0] || p?.detail || failMsg, true);
  } else { showToast(failMsg, true); }
}

// --- Task form modal -----------------------------------------------------
const taskForm = { mode: "create", editId: null, labels: [] };
let lastFocused = null;

function fieldError(name, message) {
  const err = document.querySelector(`.field-error[data-error="${name}"]`);
  if (err) err.textContent = message || "";
  const input = document.querySelector(`#taskForm [name="${name}"]`);
  if (input) input.setAttribute("aria-invalid", message ? "true" : "false");
}
function clearErrors() {
  document.querySelectorAll("#taskForm .field-error").forEach((e) => (e.textContent = ""));
  document.querySelectorAll("#taskForm [aria-invalid]").forEach((e) => e.setAttribute("aria-invalid", "false"));
}

function populateStatusSelect(selectedId) {
  const select = document.getElementById("fStatus");
  select.innerHTML = "";
  for (const c of state.board.columns) {
    const opt = el("option", { value: c.id }, c.name);
    if (c.id === selectedId) opt.selected = true;
    select.appendChild(opt);
  }
}
function populateAssigneeSelect(selectedId) {
  const select = document.getElementById("fTaskAssignee");
  select.innerHTML = "";
  select.appendChild(el("option", { value: "" }, "Unassigned"));
  for (const a of state.board.assignees) {
    const isMe = a.id === state.me?.id;
    const opt = el("option", { value: a.id }, (a.name || a.id) + (isMe ? " (You)" : ""));
    if (a.id === selectedId) opt.selected = true;
    select.appendChild(opt);
  }
}

function renderLabelChips() {
  const wrap = document.getElementById("labelChips");
  wrap.innerHTML = "";
  taskForm.labels.forEach((label, i) => {
    const chip = el("span", { class: "chip" }, el("span", {}, label));
    const remove = el("button", { type: "button", "aria-label": `Remove ${label}`,
      onclick: () => { taskForm.labels.splice(i, 1); renderLabelChips(); } },
      el("span", { class: "material-symbols-outlined" }, "close"));
    chip.appendChild(remove);
    wrap.appendChild(chip);
  });
}
function addLabelFromInput() {
  const input = document.getElementById("fLabelInput");
  const value = input.value.trim();
  if (value && !taskForm.labels.some((l) => l.toLowerCase() === value.toLowerCase()) && taskForm.labels.length < 20)
    taskForm.labels.push(value);
  input.value = "";
  renderLabelChips();
}

function openTaskModal(task = null, defaultColumnId = null) {
  lastFocused = document.activeElement;
  taskForm.mode = task ? "edit" : "create";
  taskForm.editId = task ? task.id : null;
  taskForm.labels = task ? [...(task.labels || [])] : [];

  document.getElementById("taskForm").reset();
  clearErrors();
  document.getElementById("taskTitle").textContent = task ? "Edit Task" : "Create New Task";
  document.getElementById("taskSubtitle").textContent = task ? "Update the task details" : "Add a task to the board";
  document.getElementById("taskSubmit").textContent = task ? "Save Changes" : "Create Task";
  document.getElementById("taskDelete").classList.toggle("hidden", !task);

  const firstColumn = state.board.columns[0]?.id ?? "";
  populateStatusSelect(task ? task.columnId : (defaultColumnId ?? firstColumn));
  // Default a new task's assignee to me when I'm one of the project's assignees (mirrors the mock).
  const meIsAssignee = state.board.assignees.some((a) => a.id === state.me?.id);
  populateAssigneeSelect(task ? (task.assigneeId ?? "") : (meIsAssignee ? state.me.id : ""));

  document.getElementById("fTitle").value = task ? task.title : "";
  document.getElementById("fTaskDescription").value = task?.description ?? "";
  document.getElementById("fPriority").value = task?.priority ?? "Medium";
  document.getElementById("fDueDate").value = task?.dueDate ?? "";
  document.getElementById("fLabelInput").value = "";
  renderLabelChips();

  // Always open the description editor in Write mode, collapsed.
  document.getElementById("taskDialog").classList.remove("desc-expanded");
  document.getElementById("descExpand").setAttribute("aria-pressed", "false");
  setDescMode("write");

  document.getElementById("taskModal").classList.remove("hidden");
  document.getElementById("fTitle").focus();
}
function closeTaskModal() {
  document.getElementById("taskModal").classList.add("hidden");
  if (lastFocused && typeof lastFocused.focus === "function") lastFocused.focus();
}

// Description editor: Write (raw Markdown textarea) ↔ Preview (rendered, safe HTML from markdown.js).
function setDescMode(mode) {
  const write = mode !== "preview";
  const ta = document.getElementById("fTaskDescription");
  const preview = document.getElementById("descPreview");
  const writeTab = document.getElementById("descWriteTab");
  const previewTab = document.getElementById("descPreviewTab");
  writeTab.classList.toggle("active", write);
  writeTab.setAttribute("aria-pressed", String(write));
  previewTab.classList.toggle("active", !write);
  previewTab.setAttribute("aria-pressed", String(!write));
  ta.classList.toggle("hidden", !write);
  preview.classList.toggle("hidden", write);
  // renderMarkdown (markdown.js) is safe by construction — it escapes input before transforming.
  if (!write) preview.innerHTML = renderMarkdown(ta.value) || '<p class="md-empty">Nothing to preview.</p>';
}
// Expand focuses the description: the dialog keeps its size, the other form sections hide, and the
// editor fills the available height. Toggled on the dialog so the CSS can restructure the whole form.
function toggleDescExpand() {
  const dialog = document.getElementById("taskDialog");
  const expanded = dialog.classList.toggle("desc-expanded");
  document.getElementById("descExpand").setAttribute("aria-pressed", String(expanded));
}

async function submitTaskForm(e) {
  e.preventDefault();
  clearErrors();
  // A label left in the input but not yet committed still counts.
  if (document.getElementById("fLabelInput").value.trim()) addLabelFromInput();

  const title = document.getElementById("fTitle").value.trim();
  if (!title) { fieldError("title", "Task title is required."); return; }

  const assigneeId = document.getElementById("fTaskAssignee").value || null;
  const dueDate = document.getElementById("fDueDate").value || null;
  const payload = {
    title,
    description: document.getElementById("fTaskDescription").value.trim() || null,
    priority: document.getElementById("fPriority").value,
    assigneeId,
    dueDate,
    labels: taskForm.labels,
    columnId: document.getElementById("fStatus").value,
  };

  const submit = document.getElementById("taskSubmit");
  submit.disabled = true;
  try {
    const editing = taskForm.mode === "edit";
    const res = editing ? await apiUpdateTask(taskForm.editId, payload) : await apiCreateTask(payload);
    if (res.status === 200 || res.status === 201) {
      closeTaskModal();
      await loadBoard();
      showToast(editing ? "Task updated." : "Task created.");
      return;
    }
    if (res.status === 400) { applyProblem(await res.json().catch(() => null), "Could not save the task."); return; }
    if (res.status === 404) { closeTaskModal(); await loadBoard(); showToast("That task no longer exists.", true); return; }
    showToast("Could not save the task.", true);
  } catch (err) {
    console.error(err);
    showToast("Could not save the task.", true);
  } finally {
    submit.disabled = false;
  }
}

function deleteCurrentTask() {
  if (taskForm.mode !== "edit") return;
  const id = taskForm.editId;
  openConfirm({
    title: "Delete task?",
    message: "This task will be permanently removed from the board.",
    confirmLabel: "Delete task",
    onConfirm: async () => {
      const res = await apiDeleteTask(id);
      closeConfirm();
      if (res.status === 204 || res.status === 404) {
        closeTaskModal();
        await loadBoard();
        showToast(res.status === 204 ? "Task deleted." : "Task no longer exists.");
      } else {
        showToast("Could not delete the task.", true);
      }
    },
  });
}

function wireTaskModal() {
  const modal = document.getElementById("taskModal");
  document.getElementById("addTask").addEventListener("click", () => openTaskModal());
  document.getElementById("taskClose").addEventListener("click", closeTaskModal);
  document.getElementById("taskCancel").addEventListener("click", closeTaskModal);
  document.getElementById("taskDelete").addEventListener("click", deleteCurrentTask);
  document.getElementById("taskForm").addEventListener("submit", submitTaskForm);
  document.getElementById("fLabelInput").addEventListener("keydown", (e) => {
    if (e.key === "Enter" || e.key === ",") { e.preventDefault(); addLabelFromInput(); }
  });
  document.getElementById("descWriteTab").addEventListener("click", () => setDescMode("write"));
  document.getElementById("descPreviewTab").addEventListener("click", () => setDescMode("preview"));
  document.getElementById("descExpand").addEventListener("click", toggleDescExpand);
  wireModalChrome(modal, closeTaskModal);
}

// --- Confirm dialog ------------------------------------------------------
let confirmAction = null;
function openConfirm({ title, message, confirmLabel, onConfirm }) {
  lastFocused = document.activeElement;
  confirmAction = onConfirm;
  document.getElementById("confirmTitle").textContent = title;
  document.getElementById("confirmDesc").textContent = message;
  document.getElementById("confirmOk").textContent = confirmLabel || "Delete";
  document.getElementById("confirmModal").classList.remove("hidden");
  document.getElementById("confirmCancel").focus();
}
function closeConfirm() {
  document.getElementById("confirmModal").classList.add("hidden");
  confirmAction = null;
  if (lastFocused && typeof lastFocused.focus === "function") lastFocused.focus();
}
function wireConfirmModal() {
  const modal = document.getElementById("confirmModal");
  document.getElementById("confirmClose").addEventListener("click", closeConfirm);
  document.getElementById("confirmCancel").addEventListener("click", closeConfirm);
  document.getElementById("confirmOk").addEventListener("click", () => { if (confirmAction) confirmAction(); });
  wireModalChrome(modal, closeConfirm);
}

// --- Task drag-to-move ---------------------------------------------------
let draggingTaskId = null;
const dragStatus = () => document.getElementById("dragStatus");

function wireTaskDrag(card) {
  card.addEventListener("dragstart", (e) => {
    draggingTaskId = card.dataset.id;
    e.dataTransfer.setData("text/plain", draggingTaskId);
    e.dataTransfer.effectAllowed = "move";
    card.classList.add("dragging");
    const title = state.board.columns.flatMap((c) => c.tasks).find((t) => t.id === draggingTaskId)?.title ?? "task";
    dragStatus().textContent = `“${title}” grabbed. Drop it on a column to move it.`;
  });
  card.addEventListener("dragend", () => {
    card.classList.remove("dragging");
    draggingTaskId = null;
    document.querySelectorAll(".column-tasks.drop-over").forEach((l) => l.classList.remove("drop-over"));
    dragStatus().textContent = "";
  });
}

function wireColumnDropTarget(list) {
  list.addEventListener("dragover", (e) => {
    if (draggingTaskId === null) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    list.classList.add("drop-over");
  });
  list.addEventListener("dragleave", (e) => {
    if (!list.contains(e.relatedTarget)) list.classList.remove("drop-over");
  });
  list.addEventListener("drop", async (e) => {
    e.preventDefault();
    list.classList.remove("drop-over");
    const id = draggingTaskId || e.dataTransfer.getData("text/plain");
    if (!id) return;
    const columnId = list.dataset.column;
    const position = dropIndex(list, e.clientY);
    const res = await apiMoveTask(id, { columnId, position });
    if (res.ok) { await loadBoard(); } else { showToast("Could not move the task.", true); }
  });
}

// The index at which to insert, based on the pointer's Y against each card's midpoint.
function dropIndex(list, clientY) {
  const cards = [...list.querySelectorAll(".task-card:not(.dragging)")];
  for (let i = 0; i < cards.length; i++) {
    const box = cards[i].getBoundingClientRect();
    if (clientY < box.top + box.height / 2) return i;
  }
  return cards.length;
}

// --- Load + wiring -------------------------------------------------------
async function loadBoard() {
  const board = await apiBoard();
  if (board === null) {
    document.getElementById("board").innerHTML = "";
    document.getElementById("boardTitle").textContent = "Project not found";
    document.getElementById("boardSubtitle").textContent = "It may have been deleted, or you don't have access.";
    document.getElementById("addTask").disabled = true;
    return;
  }
  state.board = board;
  renderBoardHead();
  renderBoard();
}

function wireStaticHandlers() {
  document.getElementById("csrfField").value = decodeURIComponent(getCookie("XSRF-TOKEN") || "");

  const search = document.getElementById("searchInput");
  document.getElementById("searchForm").addEventListener("submit", (e) => e.preventDefault());
  search.addEventListener("input", () => {
    state.filter = search.value.trim().toLowerCase();
    if (state.board) renderBoard();
  });
}

async function init() {
  wireStaticHandlers();
  wireTaskModal();
  wireConfirmModal();

  if (!projectId) { window.location.assign("index.html"); return; }
  try {
    state.me = await apiMe();
    if (!state.me) { redirectToLogin(); return; }
    renderHeaderUser(state.me);
    await loadBoard();
  } catch (err) {
    console.error(err);
    showToast("Something went wrong loading the board.", true);
  }
}

document.addEventListener("DOMContentLoaded", init);
