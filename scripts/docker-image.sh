#!/usr/bin/env bash
# docker-image.sh — build (and optionally push) the multi-arch m0lte/packet.net node image.
#
#   scripts/docker-image.sh <version>          # build host-arch only, load into local docker
#   scripts/docker-image.sh <version> push     # build amd64+arm64 and push (needs docker login)
#
# Publishes the self-contained app tree with PDN_FAST (no R2R/single-file) — fast and, for
# arm64, NO crossgen, so it sidesteps the OOM that the .deb's R2R cross-publish risks. buildx
# selects pdn-app/<arch>/ per platform via TARGETARCH (see docker/node/Dockerfile).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${1:?usage: docker-image.sh <version> [push]}"
mode="${2:-load}"
image=m0lte/packet.net

# docker arch -> dotnet RID
declare -A RID=( [amd64]=linux-x64 [arm64]=linux-arm64 )

# Which arches to stage: both when pushing, host-arch only for a local load.
if [ "$mode" = push ]; then
  arches=(amd64 arm64)
else
  case "$(uname -m)" in
    x86_64) arches=(amd64) ;;
    aarch64) arches=(arm64) ;;
    *) echo "unsupported host arch $(uname -m)" >&2; exit 1 ;;
  esac
fi

rm -rf "$root/pdn-app"
for arch in "${arches[@]}"; do
  echo "==> publish $arch (${RID[$arch]})"
  PDN_FAST=1 "$root/scripts/build-deb.sh" "${RID[$arch]}" "$version"
  mkdir -p "$root/pdn-app/$arch"
  cp -a "$root/artifacts/deb/${RID[$arch]}/opt/packetnet/app/." "$root/pdn-app/$arch/"
done

if [ "$mode" = push ]; then
  echo "==> buildx multi-arch push $image:$version + :latest"
  docker buildx build -f "$root/docker/node/Dockerfile" \
    --platform linux/amd64,linux/arm64 \
    -t "$image:$version" -t "$image:latest" \
    --push "$root"
else
  echo "==> buildx local load $image:$version (${arches[0]})"
  docker buildx build -f "$root/docker/node/Dockerfile" \
    --platform "linux/${arches[0]}" \
    -t "$image:$version" \
    --load "$root"
fi
