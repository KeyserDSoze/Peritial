#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
docker compose -f docker/compose.yml up --build -d
docker compose -f docker/compose.yml ps
printf 'Demo local Peritial: http://localhost:5173\n'
printf 'Never reuse demo credentials in production.\n'