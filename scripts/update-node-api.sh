#!/usr/bin/env bash
# Regenerate the route inventory table in docs/node-api.md from the node's own
# EndpointDataSource. Run it after adding, moving or renaming a route, or after
# changing the scope policy on one.
#
# The generator IS the guard test: RouteInventoryTests boots the real composition
# root, reads the endpoint table, and compares it against the block between the
# BEGIN/END markers in the doc. With PDN_WRITE_ROUTE_TABLE=1 it writes the block
# first, so the same code both produces and checks the doc and the two cannot
# disagree. See docs/node-api.md for why there is no hand-written OpenAPI file.
set -euo pipefail

cd "$(dirname "$0")/.."

PDN_WRITE_ROUTE_TABLE=1 dotnet test tests/Packet.Node.Tests/Packet.Node.Tests.csproj \
  --filter "FullyQualifiedName~RouteInventoryTests" \
  "$@"

echo
git --no-pager diff --stat -- docs/node-api.md
