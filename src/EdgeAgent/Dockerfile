# The edge agent (Phase 7): self-contained on the ordinary runtime, neither Native AOT nor trimmed
# (ADR-0018), on runtime-deps — the image carries no .NET install; the agent brings its own.
# Build context: the repository root, as a committed tree — use deploy/build-images.sh.
#
# linux-x64 and linux-arm64 are one build, chosen by the platform the image is built for: TARGETARCH
# comes from the builder and the publish profiles are named after the RIDs, so the RID cannot be
# forgotten and a build on the edge machine itself needs no flag. An architecture with neither
# profile is refused rather than guessed at, and the last stage checks that the binary really is the
# architecture it shipped in — the choice above is not allowed to go wrong quietly either.
#
# The SDK stage runs on the machine doing the build (BUILDPLATFORM), not on the target: an arm64
# image built on an x64 server is a cross-publish that takes a minute, where running that stage under
# emulation takes a quarter of an hour. Both arguments below are BuildKit's and deliberately have no
# default — a default wins over the platform the build was actually asked for, which is how an
# `-arm64` tag ends up on an x64 binary. So BuildKit is required, and deploy/build-images.sh says so
# in a sentence when Docker has no buildx plugin.

ARG BUILDPLATFORM

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
# The profile names are the RIDs.
RUN case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "no publish profile for TARGETARCH=$TARGETARCH" >&2; exit 1 ;; \
    esac; \
    dotnet publish src/EdgeAgent/ScadaDarbox.EdgeAgent.csproj -p:PublishProfile="$rid" --output /out --nologo && \
    echo "$rid" > /rid

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.11
WORKDIR /app
COPY --from=build /out ./
COPY --from=build /rid /rid
# What is in this image has to be what this image is. Both are decided by the build — the profile by
# TARGETARCH, the architecture by the platform — so this pair is the one thing that could disagree
# with nothing looking wrong, and an x64 binary under an `-arm64` tag is exactly the mistake that
# would reach a plant and be nobody's fault.
RUN rid=$(cat /rid); \
    case "$(uname -m)" in \
      x86_64) here=linux-x64 ;; \
      aarch64) here=linux-arm64 ;; \
      *) echo "no publish profile for this image's architecture: $(uname -m)" >&2; exit 1 ;; \
    esac; \
    if [ "$rid" != "$here" ]; then \
      echo "this image is $here, but the binary in it was published for $rid" >&2; exit 1; \
    fi; \
    rm /rid
# pki/: the OPC UA driver's client certificate and trust lists, created on first use beside the
# binaries. /data: the buffer (ADR-0018) — on a volume, so it survives the container. Both
# writable by the app user, and nothing else here is.
RUN mkdir pki /data && chown "$APP_UID" pki /data
USER $APP_UID
ENTRYPOINT ["/app/ScadaDarbox.EdgeAgent"]
