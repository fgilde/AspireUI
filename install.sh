#!/usr/bin/env bash
# Self-host installer for AspireUI. Works two ways:
#   • inside a checkout:      ./install.sh
#   • one-liner on any host:  bash -c "$(curl -fsSL https://raw.githubusercontent.com/fgilde/AspireUI/master/install.sh)"
# In the one-liner case it clones (or updates) the repo, then builds and starts the container via
# docker compose. Idempotent — safe to re-run to update.
set -euo pipefail

REPO="https://github.com/fgilde/AspireUI.git"
DIR="${ASPIREUI_DIR:-$HOME/aspireui}"

if ! command -v docker >/dev/null 2>&1; then
  echo "Error: Docker is not installed. Install it first: https://docs.docker.com/engine/install/" >&2
  exit 1
fi
if ! docker compose version >/dev/null 2>&1; then
  echo "Error: 'docker compose' (v2 plugin) is not available. Install it: https://docs.docker.com/compose/install/" >&2
  exit 1
fi

# If we're already inside an AspireUI checkout, build from here; otherwise fetch the repo first
# (the one-liner case, where the script runs from stdin and there are no repo files around it).
if [ -f "docker-compose.yml" ] && [ -d "src/AspireUI.Server" ]; then
  echo "Using the current checkout."
else
  if ! command -v git >/dev/null 2>&1; then
    echo "Error: git is required to fetch AspireUI. Install git or run this from a checkout." >&2
    exit 1
  fi
  if [ -d "$DIR/.git" ]; then
    echo "Updating AspireUI in $DIR ..."
    git -C "$DIR" pull --ff-only
  else
    echo "Cloning AspireUI into $DIR ..."
    git clone --depth 1 "$REPO" "$DIR"
  fi
  cd "$DIR"
fi

docker compose up -d --build

echo
echo "AspireUI is starting at http://localhost:8080"
echo "First run builds the SPA inside the container — give it a minute before the page loads."
echo "Follow startup:  docker compose logs -f"
