# The edge agent (Phase 7): self-contained on the ordinary runtime, neither Native AOT nor trimmed
# (ADR-0018), on runtime-deps — the image carries no .NET install; the agent brings its own.
# Build context: the repository root, as a committed tree — use deploy/build-images.sh.
#
# linux-x64 only. linux-arm64 is unverified (ADR-0018) and must be built and run before edge
# hardware is chosen.

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/EdgeAgent/ScadaDarbox.EdgeAgent.csproj -p:PublishProfile=linux-x64 --output /out --nologo

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.11
WORKDIR /app
COPY --from=build /out ./
# pki/: the OPC UA driver's client certificate and trust lists, created on first use beside the
# binaries. /data: the buffer (ADR-0018) — on a volume, so it survives the container. Both
# writable by the app user, and nothing else here is.
RUN mkdir pki /data && chown "$APP_UID" pki /data
USER $APP_UID
ENTRYPOINT ["/app/ScadaDarbox.EdgeAgent"]
