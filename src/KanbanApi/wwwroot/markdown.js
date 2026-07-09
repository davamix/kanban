"use strict";

// A small, safe-by-construction Markdown renderer for the task Description preview. Deliberately a
// *subset* (headings, bold/italic, inline + fenced code, links, blockquote, unordered/ordered lists,
// hr, paragraphs) -- enough for a task description, without pulling in a parser dependency.
//
// Security model (ASVS V5 -- output encoding): the entire input is HTML-**escaped first** (via
// escapeHtml from common.js), so no raw HTML or attributes from the user can ever survive into the
// output. Every transform below emits only a fixed set of tags, and the *only* attribute we inject is
// a link href, which is scheme-checked (http/https/mailto/relative only) -- so javascript:/data:
// URLs, <script>, onerror=, etc. all render as inert text. Correctness bugs in the subset are
// therefore never an XSS vector; the worst case is imperfect formatting.
//
// Code spans/blocks are lifted to sentinel placeholders (@@BLOCK0@@ / @@CODE0@@) so their contents
// aren't re-parsed. Even if a user typed one verbatim, it can only mis-render, never inject markup.

function renderMarkdown(src) {
  if (!src) return "";
  let text = escapeHtml(String(src)).replace(/\r\n?/g, "\n");

  // 1) Fenced code blocks (``` ... ```) -> protected <pre><code>. Contents are already escaped.
  const blocks = [];
  text = text.replace(/```[^\n]*\n([\s\S]*?)```/g, (_, code) => {
    blocks.push(`<pre class="md-pre"><code>${code.replace(/\n$/, "")}</code></pre>`);
    return `@@BLOCK${blocks.length - 1}@@`;
  });

  // 2) Block-level pass, line by line.
  const lines = text.split("\n");
  const html = [];
  let paragraph = [];
  const flushPara = () => {
    if (paragraph.length) { html.push(`<p>${inlineMarkdown(paragraph.join(" "))}</p>`); paragraph = []; }
  };

  for (let i = 0; i < lines.length; ) {
    const line = lines[i];

    if (/^@@BLOCK\d+@@$/.test(line.trim())) { flushPara(); html.push(line.trim()); i++; continue; }
    if (/^\s*$/.test(line)) { flushPara(); i++; continue; }
    // Horizontal rule: 3+ of - * _ (optionally spaced).
    if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) { flushPara(); html.push("<hr>"); i++; continue; }

    const heading = line.match(/^(#{1,6})\s+(.*)$/);
    if (heading) { flushPara(); const lvl = heading[1].length; html.push(`<h${lvl}>${inlineMarkdown(heading[2].trim())}</h${lvl}>`); i++; continue; }

    // Blockquote (`>` becomes `&gt;` after escaping). Consecutive quoted lines join into one.
    if (/^&gt;\s?/.test(line)) {
      flushPara();
      const quote = [];
      while (i < lines.length && /^&gt;\s?/.test(lines[i])) { quote.push(lines[i].replace(/^&gt;\s?/, "")); i++; }
      html.push(`<blockquote>${inlineMarkdown(quote.join(" "))}</blockquote>`);
      continue;
    }

    if (/^\s*[-*]\s+/.test(line)) {
      flushPara();
      const items = [];
      while (i < lines.length && /^\s*[-*]\s+/.test(lines[i])) { items.push(`<li>${inlineMarkdown(lines[i].replace(/^\s*[-*]\s+/, ""))}</li>`); i++; }
      html.push(`<ul>${items.join("")}</ul>`);
      continue;
    }
    if (/^\s*\d+\.\s+/.test(line)) {
      flushPara();
      const items = [];
      while (i < lines.length && /^\s*\d+\.\s+/.test(lines[i])) { items.push(`<li>${inlineMarkdown(lines[i].replace(/^\s*\d+\.\s+/, ""))}</li>`); i++; }
      html.push(`<ol>${items.join("")}</ol>`);
      continue;
    }

    paragraph.push(line.trim());
    i++;
  }
  flushPara();

  // 3) Restore protected code blocks.
  return html.join("\n").replace(/@@BLOCK(\d+)@@/g, (_, n) => blocks[Number(n)]);
}

// Inline spans within a block. Input is already HTML-escaped.
function inlineMarkdown(s) {
  // Inline code first, so its contents are shielded from the emphasis/link rules.
  const codes = [];
  s = s.replace(/`([^`]+)`/g, (_, c) => { codes.push(`<code>${c}</code>`); return `@@CODE${codes.length - 1}@@`; });

  // Links [text](url) -- lift the finished anchor to a sentinel too, so the emphasis passes below
  // can't rewrite the href contents (a URL with * or _ would otherwise be corrupted). href is the
  // only injected attribute, and it is scheme-checked.
  const links = [];
  s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (whole, txt, url) => {
    const safe = safeUrl(url);
    if (!safe) return whole;
    links.push(`<a href="${safe}" target="_blank" rel="noopener noreferrer">${txt}</a>`);
    return `@@LINK${links.length - 1}@@`;
  });

  s = s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  s = s.replace(/__([^_]+)__/g, "<strong>$1</strong>");
  s = s.replace(/\*([^*]+)\*/g, "<em>$1</em>");
  s = s.replace(/(^|[^\w])_([^_\s][^_]*)_(?=[^\w]|$)/g, "$1<em>$2</em>");

  // Restore links before code so a code sentinel that sat inside link text resolves afterwards.
  s = s.replace(/@@LINK(\d+)@@/g, (_, n) => links[Number(n)]);
  return s.replace(/@@CODE(\d+)@@/g, (_, n) => codes[Number(n)]);
}

// Allow only safe link targets: http(s), mailto, or a *same-origin* relative path / anchor. The url
// is already HTML-escaped (so quotes can't break out of the attribute); this only gates the scheme.
// Reject protocol-relative (//host) and backslash (/\host) forms — the browser resolves those to an
// external origin (open redirect / phishing) — and anything with another scheme (javascript:, data:,
// vbscript:, ...) so the link renders as plain text.
function safeUrl(url) {
  const u = url.trim();
  if (/^(https?:|mailto:)/i.test(u)) return u;
  if (/^[/#]/.test(u) && !/^\/[/\\]/.test(u)) return u;
  return null;
}
