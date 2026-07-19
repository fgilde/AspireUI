#!/usr/bin/env bash
# Self-host installer for AspireUI: builds and starts the container via
# docker compose. Safe to re-run (idempotent).
set -euo pipefail

if ! command -v docker >/dev/null 2>&1; then
  echo "Error: Docker is not installed. Install it first: https://docs.docker.com/engine/install/" >&2
  exit 1
fi

if ! docker compose version >/dev/null 2>&1; then
  echo "Error: 'docker compose' (v2 plugin) is not available. Install the Docker Compose plugin: https://docs.docker.com/compose/install/" >&2
  exit 1
fi

cd "$(dirname "$0")"
docker compose up -d --build

echo "AspireUI running at http://localhost:8080"
