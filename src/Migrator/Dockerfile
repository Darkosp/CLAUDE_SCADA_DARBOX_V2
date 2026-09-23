# The one-shot migrator (ADR-0012, ADR-0014). The only image that is ever given the privileged
# connection string. Build context: the repository root, via deploy/build-images.sh.

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Migrator/ScadaDarbox.Migrator.csproj --configuration Release --output /out --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0.11
# Npgsql probes for Kerberos (GSSAPI) when it opens a connection. Without the library, .NET
# prints "Error: libgssapi_krb5.so.2: cannot open shared object file" — harmless, since nothing
# here uses Kerberos, but a false "Error:" in a deployment log is how real ones get ignored.
RUN apt-get update  && apt-get install --yes --no-install-recommends libgssapi-krb5-2  && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
USER $APP_UID
ENTRYPOINT ["dotnet", "ScadaDarbox.Migrator.dll"]
