#!/usr/bin/env bash
# Builds every image from a commit — never from the working directory (Phase 6).
#
#   deploy/build-images.sh [commit] [arch]      default: HEAD and x64
#
#   x64     all five images, for the architecture of the machine doing the build.
#   arm64   the edge agent only, and for linux/arm64: it is the only image an edge machine runs,
#           the rest of the topology being on the server. Tagged with an `-arm64` suffix, so an
#           arm64 image never quietly replaces an x64 one under the same tag.
#
# On an arm64 machine, build with no arguments at all: there is nothing to cross, the images come
# out arm64, and they carry the plain tag. Building for arm64 from a machine that is not arm64 needs
# binfmt as well, because the image's own architecture check then runs emulated — the SDK does not:
# it runs on this machine and cross-publishes for the target, which is why an arm64 image costs a
# minute here rather than a quarter of an hour.
#
#   docker run --privileged --rm tonistiigi/binfmt --install arm64
#
# Docker Desktop has that and the buildx plugin already. Anywhere else the plugin is a package:
# `docker-buildx` on Ubuntu, `docker-buildx-plugin` from Docker's own repository. Every build here
# goes through BuildKit, because these images are built for a platform rather than for whatever the
# daemon happens to be, and because BuildKit is what makes the cross-build fast.
# The build context is `git archive` of that commit: exactly the tracked files as committed.
# Uncommitted edits, untracked files and local build output cannot reach an image, which is the
# point — an image that depends on the state of one machine's checkout proves nothing about the
# next one. Each image is tagged with the commit it came from and labelled with its full hash.
set -euo pipefail

ref="${1:-HEAD}"
arch="${2:-x64}"
cd "$(git rev-parse --show-toplevel)"
commit="$(git rev-parse --verify "${ref}^{commit}")"
tag="$(git rev-parse --short "$commit")"

platform=""
suffix=""
if ! docker buildx version >/dev/null 2>&1; then
  echo "Building an image needs BuildKit, and this Docker has no buildx plugin." >&2
  echo "      Install it: docker-buildx on Ubuntu, or docker-buildx-plugin from Docker's repository." >&2
  exit 3
fi

# The machine's own architecture: the x64 mode means "for this machine", whatever it is.
case "$(uname -m)" in
  x86_64 | amd64) host=linux/amd64 ;;
  aarch64 | arm64) host=linux/arm64 ;;
  *)
    echo "unknown build machine: $(uname -m)" >&2
    exit 3
    ;;
esac

platform="$host"
case "$arch" in
  x64)
    ;;
  arm64)
    platform=linux/arm64
    suffix="-arm64"
    if [ "$host" != "linux/arm64" ]; then
      echo "note: this is a cross-build: the SDK runs here and cross-publishes, but the image's" >&2
      echo "      own architecture check runs emulated, so arm64 needs binfmt where the daemon" >&2
      echo "      runs: docker run --privileged --rm tonistiigi/binfmt --install arm64" >&2
    fi
    ;;
  *)
    echo "usage: deploy/build-images.sh [commit] [x64|arm64]" >&2
    exit 2
    ;;
esac

if [ "$ref" = "HEAD" ] && [ -n "$(git status --porcelain)" ]; then
  echo "note: the working directory has uncommitted changes; they are not in these images." >&2
fi

build() {
  local name="$1" dockerfile="$2"
  echo "==> scada-darbox/${name}:${tag}${suffix}  (${dockerfile} at ${commit}, ${platform})"
  # --load: the image has to land here, in the local store, or nothing can run it. The archive is
  # piped in as the build context: buildx reads a `-` context from stdin, and without the pipe it
  # would sit there waiting for one instead of building.
  git archive --format=tar "$commit" |
    docker buildx build --platform "$platform" --load \
      --file "$dockerfile" \
      --label "org.opencontainers.image.revision=${commit}" \
      --tag "scada-darbox/${name}:${tag}${suffix}" \
      -
}

if [ "$arch" = "arm64" ]; then
  build edge-agent src/EdgeAgent/Dockerfile
  echo "Built scada-darbox/edge-agent:${tag}${suffix} from ${commit} (${platform})."
else
  build migrator        src/Migrator/Dockerfile
  build gateway         src/Gateway/Dockerfile
  build modbus-sim      tools/ModbusSimulator/Dockerfile
  build opcua-sim       tools/OpcUaSimulator/Dockerfile
  build edge-agent      src/EdgeAgent/Dockerfile

  echo "Built scada-darbox/{migrator,gateway,modbus-sim,opcua-sim,edge-agent}:${tag} from ${commit}."
fi
