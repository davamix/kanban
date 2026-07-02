# Releasing (publishing the container image)

Image publishing is **release-driven**: pushing a `v*` version tag triggers the CD workflow
([.github/workflows/cd.yml](../.github/workflows/cd.yml)), which re-runs the correctness/security
gates, builds the image, runs the Trivy scan as a **hard gate**, and only then pushes to **GHCR**
(`ghcr.io/davamix/kanban`). No image is published on ordinary pushes to `main`.

## Cut a release

```bash
git checkout main && git pull        # ship the exact commit you intend
git tag -a v0.1.0 -m "v0.1.0"        # next version (see below); annotated
git push origin v0.1.0               # ← triggers verify → build → scan → publish
```

The image is built **in CI**, not locally. Watch the run: `gh run watch` (or the Actions tab).

## Versioning

Semantic versioning; each release is a **new, higher** tag — a published tag is never moved or
reused:

| Bump | When |
|---|---|
| patch `v0.1.1` | fixes, dependency bumps — no behaviour change |
| minor `v0.2.0` | new features (the usual release while in `0.x`) |
| major `v1.0.0` | once the API / contract is declared stable |

## What gets published

For tag `vX.Y.Z`, a successful run pushes:

- `ghcr.io/davamix/kanban:X.Y.Z` and `:X.Y`
- `ghcr.io/davamix/kanban:sha-<commit>`
- `ghcr.io/davamix/kanban:latest` (re-pointed to this build)

The integrated stack ([ecosystem-platform](https://github.com/davamix/ecosystem-platform)) pins
`:latest`, so `docker compose pull` there picks up the newest release.

> **First publish only:** the GHCR package is created **private**. Make it public (Package →
> Settings → *Change visibility*) or `docker login ghcr.io` on the host that runs the stack.

## Ad-hoc rebuild (no version bump)

To re-publish `:latest` without minting a version (e.g. after a base-image CVE fix), run the
workflow manually — `gh workflow run CD` (or the Actions *Run workflow* button). This is the
exception; releases are tag-driven.
