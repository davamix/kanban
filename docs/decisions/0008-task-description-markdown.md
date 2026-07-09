# ADR 0008 — Markdown editor for the task Description

- **Status:** Accepted (2026-07-09)
- **Related:** [0007-task-board.md](0007-task-board.md), [../security/asvs-l2/](../security/asvs-l2/)
- **Stitch:** "Task Creation Form - Final Harmonization" — the Description field's `Write` / `Preview` /
  `Expand editor` controls (project `12395060896990119837`).

## Context

The task form's Description field in the Stitch design carries three controls — a Markdown (Write)
mode, a Preview mode, and an Expand-editor toggle — implying the field accepts **Markdown**, edited as
raw text and viewable rendered. ADR 0007 shipped the board with a plain textarea; this increment adds
the Markdown editing/preview affordance. The question that shaped the design was **not** "which parser"
but that *rendering Markdown is an XSS sink* (Markdown allows raw HTML and `javascript:` links), and
the app currently has no Content-Security-Policy backstop.

## Decisions

1. **Store raw Markdown; no schema/API change.** `TaskItem.Description` already holds free text — that
   raw Markdown stays the source of truth. Rendering is purely a display concern, done client-side.

2. **No parser dependency — a small, safe-by-construction subset renderer** (`wwwroot/markdown.js`,
   `renderMarkdown`). It supports the common subset a task needs: headings, `**bold**`/`*italic*`,
   inline + fenced code, links, blockquote, unordered/ordered lists, hr, paragraphs. This honours the
   project's lean/no-toolchain rule; full CommonMark/tables can graduate to a vendored
   `marked`+`DOMPurify` later if needed.

3. **Safety comes from escape-first, not from the parser (ASVS V5).** The renderer HTML-**escapes the
   entire input first** (via the shared `escapeHtml`), *then* applies whitelist transforms that emit
   only a fixed tag set. So no raw HTML/attributes from the user can survive, and a subset
   *correctness* bug can never be an injection — the worst case is imperfect formatting. The **only**
   injected attribute is a link `href`, gated by a scheme allow-list (`http`/`https`/`mailto`/relative);
   `javascript:`, `data:`, etc. render as inert text. Code spans/blocks are lifted to sentinel
   placeholders so their contents aren't re-parsed.

4. **UI: a Write/Preview segmented toggle + an Expand button** on the Description field. Write shows the
   raw-Markdown textarea; Preview renders it into a read-only pane. **Expand is a focus mode**: the
   dialog keeps its size while the other form sections (and the title) hide, letting the Description —
   Write or Preview — fill the available height, rather than growing the modal taller. The editor
   resets to **Write, collapsed** each time the form opens; submit still reads `textarea.value`
   unchanged.

5. **Author-preview only, for now.** The rendered HTML is shown only in the author's own Preview — the
   card and board don't render descriptions — so today's blast radius is self-only. Sanitization is
   built in regardless, so a future read-only task-detail view (rendering to *other* members) inherits
   the same guarantee without rework.

## Why

- **Escape-first over a sanitizer library:** for a bounded subset, escaping up front is simpler to
  reason about and audit than parsing-then-sanitizing, and needs no dependency. It's safe even if the
  transform rules are imperfect.
- **Client-side render:** Preview is instant and needs no round-trip; server-side rendering (Markdig +
  sanitizer) would add two NuGet deps and a request per keystroke for no benefit at this scope.
- **Subset, not full GFM:** task descriptions rarely need tables/footnotes; the subset keeps the
  renderer ~110 lines and reviewable.

## Consequences

- New `wwwroot/markdown.js` (loaded by `board.html` before `board.js`); the Description field gains a
  toolbar + preview pane; `board.js` gains `setDescMode`/`toggleDescExpand` and resets editor state on
  open; Markdown preview + editor styles added to `styles.css`. No backend, migration, or API change.
- **No automated test** — there is no JS engine in the dev container. The renderer is kept small and
  reviewable; a manual XSS/render checklist is used (payloads like `<img src=x onerror=alert(1)>`,
  `[x](javascript:alert(1))`, raw `<script>`/`<b>` must all render inert). If a JS test runner is
  added later, `renderMarkdown` is a pure function and trivially unit-testable.
- Deferred: full CommonMark/GFM (tables, task-list checkboxes), rendering the description read-only on
  a task-detail view, and a Content-Security-Policy as defence-in-depth.
