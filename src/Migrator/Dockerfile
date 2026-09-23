# The one-shot migrator (ADR-0012, ADR-0014). The only image that is ever given the privileged
# connection string. Build context: the repository root, via deploy/build-images.sh.

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Migrator/ScadaDarbox.Migrator.csproj --configuration Release --output /out --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0.11
WORKDIR /app
COPY --from=build /out ./
USER $APP_UID
ENTRYPOINT ["dotnet", "ScadaDarbox.Migrator.dll"]
