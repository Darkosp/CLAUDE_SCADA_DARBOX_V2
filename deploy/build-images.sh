#!/usr/bin/env bash
# Builds every image from a commit — never from the working directory (Phase 6).
#
#   deploy/build-images.sh [commit]      default: HEAD
#
# The build context is `git archive` of that commit: exactly the tracked files as committed.
# Uncommitted edits, untracked files and local build output cannot reach an image, which is the
# point — an image that depends on the state of one machine's checkout proves nothing about the
# next one. Each image is tagged with the commit it came from and labelled with its full hash.
set -euo pipefail

ref="${1:-HEAD}"
cd "$(git rev-parse --show-toplevel)"
commit="$(git rev-parse --verify "${ref}^{commit}")"
tag="$(git rev-parse --short "$commit")"

if [ "$ref" = "HEAD" ] && [ -n "$(git status --porcelain)" ]; then
  echo "note: the working directory has uncommitted changes; they are not in these images." >&2
fi

build() {
  local name="$1" dockerfile="$2"
  echo "==> scada-darbox/${name}:${tag}  (${dockerfile} at ${commit})"
  git archive --format=tar "$commit" |
    docker build --file "$dockerfile" \
      --label "org.opencontainers.image.revision=${commit}" \
      --tag "scada-darbox/${name}:${tag}" \
      -
}

build migrator        src/Migrator/Dockerfile
build gateway         src/Gateway/Dockerfile
build modbus-sim      tools/ModbusSimulator/Dockerfile
build opcua-sim       tools/OpcUaSimulator/Dockerfile
build edge-agent      src/EdgeAgent/Dockerfile

echo "Built scada-darbox/{migrator,gateway,modbus-sim,opcua-sim,edge-agent}:${tag} from ${commit}."
