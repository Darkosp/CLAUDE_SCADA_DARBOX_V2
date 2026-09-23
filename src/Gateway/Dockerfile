# The Gateway, serving the built web client from its own origin (Phase 6).
# Build context: the repository root, as a committed tree — use deploy/build-images.sh, which
# builds from `git archive` so nothing uncommitted can reach the image.

FROM node:24.19.0-bookworm-slim AS web
WORKDIR /web
COPY src/Web/package.json src/Web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/Web/ ./
RUN npx ng build --configuration production

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Gateway/ScadaDarbox.Gateway.csproj --configuration Release --output /out --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11
# Npgsql probes for Kerberos (GSSAPI) when it opens a connection. Without the library, .NET
# prints "Error: libgssapi_krb5.so.2: cannot open shared object file" — harmless, since nothing
# here uses Kerberos, but a false "Error:" in a deployment log is how real ones get ignored.
RUN apt-get update  && apt-get install --yes --no-install-recommends libgssapi-krb5-2  && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
# ASP.NET Core serves {content root}/wwwroot; the content root is this directory.
COPY --from=web /web/dist/ScadaDarboxWeb/browser ./wwwroot
# The OPC UA driver keeps its client certificate and trust lists in pki/ beside the binaries,
# and creates it on first use. Everything else here is root-owned and read-only to the app
# user, so without this every OPC UA device reads Bad.
RUN mkdir pki && chown "$APP_UID" pki
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ScadaDarbox.Gateway.dll"]
