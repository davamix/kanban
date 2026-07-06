# Kanban design system

The Kanban SPA is vanilla HTML/CSS/JS (no Tailwind/Node — the lean-stack rule). Visual design
follows the Stitch **"Kanban Dashboard"** project and the *KanbanFlow* design-system document;
this file is the local source of truth for how those map onto our code.

## Tokens are the single source of truth

All colours, spacing, type, shape, and layout live in
[`wwwroot/tokens.css`](../src/KanbanApi/wwwroot/tokens.css) as CSS custom properties. Components in
[`styles.css`](../src/KanbanApi/wwwroot/styles.css) reference the tokens — **never** hardcode a hex,
px, or font-family that duplicates a token. Add a token before reaching for a literal.

## Brand

| Aspect | Value | Note |
|---|---|---|
| Primary | `#2563EB` (Core Blue) | `--color-primary`; hover `#1d4ed8` |
| Status | Success `#059669` · Warning `#D97706` · Error `#b91c1c` | DONE / WIP / overdue |
| Neutrals | Slate scale | shared with Calendar |
| Type | Inter | headings semibold/bold, tight tracking |
| Shape | flat / square — 0 radius, border-led, no shadows (`--radius` / `--radius-sm`) | matches the Streamlined Layout screen (and Calendar) |

**Divergence from Calendar** (documented in
[decisions/0003-project-domain.md](decisions/0003-project-domain.md)): Kanban's primary is
`#2563EB` (Calendar `#004ac6`) — a single-token knob (`--color-primary`). Everything else (the
flat/square shape, neutral scale, Material-3 token naming, Inter) is shared.

## Components (this screen)

- **App shell:** sticky top header (brand · search · user + logout) over a full-width content area.
  The left sidebar was removed with the drag-to-delete refinement (ADR 0005); a bottom nav still
  shows below `--sidebar-breakpoint`.
- **Stat tile:** icon chip + label + value, on a bordered surface card.
- **Project card:** icon + name + role **badge** (`Owner` / `Shared`), 2-line description,
  footer with an **avatar stack** (initials) + the date range. The whole card is the click target
  (keyboard-focusable). Adapts the mockup's task-count/efficiency bits — which have no domain data
  yet — down to what a project actually carries.
- **Create-project modal:** a centred dialog over a scrim (the Stitch *Project Creation Form*),
  opened by "Create New". Three token-styled `form-section`s (General Information · Stakeholders ·
  Resources & Timeline), each a `section-head` (primary icon + uppercase label) over its fields.
  The **assignee picker** is a `select` that moves each pick into a removable `chip`; "Client /
  Organization" is a disabled placeholder (no domain field yet). Dismisses on Cancel, ✕, backdrop,
  or Escape. Submits via `POST /api/projects` with the `X-CSRF-TOKEN` header; field errors render
  inline from the RFC 9457 `errors`.
- **Logout** is a real antiforgery-protected POST form + button, never a link.

## Accessibility

Interactive controls carry accessible labels/titles; the card is `tabindex="0"` with a visible
focus ring; not-yet-built areas advertise "coming soon" rather than dead-ending. All user-supplied
text is escaped (`escapeHtml`) before it hits the DOM.
