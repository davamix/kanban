#!/usr/bin/env bash
# Runs once when the dev container is created (see devcontainer.json postCreateCommand).
set -euo pipefail

# 1. Hydrate the shared `standards` submodule at the pinned commit.
git submodule update --init .standards

# 2. Install gitleaks (secret scanner) if it isn't already available.
if ! command -v gitleaks >/dev/null 2>&1; then
  ver="$(curl -fsSL https://api.github.com/repos/gitleaks/gitleaks/releases/latest \
        | sed -n 's/.*"tag_name": *"v\([^"]*\)".*/\1/p' | head -n1)"
  ver="${ver:-8.21.2}"   # fallback if the API is unreachable
  os="$(uname -s | tr '[:upper:]' '[:lower:]')"
  arch="$(uname -m)"; case "$arch" in x86_64) arch=x64;; aarch64|arm64) arch=arm64;; esac
  echo "Installing gitleaks v${ver} (${os}_${arch})…"
  curl -fsSL "https://github.com/gitleaks/gitleaks/releases/download/v${ver}/gitleaks_${ver}_${os}_${arch}.tar.gz" \
    | sudo tar -xz -C /usr/local/bin gitleaks
fi

# 3. Enable the shared git hooks (pre-commit secret scan, version-controlled in .githooks/).
git config core.hooksPath .githooks

echo "Dev container ready — standards hydrated, gitleaks $(gitleaks version 2>/dev/null || echo '?'), hooks enabled."
