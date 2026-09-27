#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
dotnet run --project src/ActivityTracker.Api --launch-profile ActivityTracker.Api
