# The OPC UA simulator. Not part of the product. Its endpoint URL names "localhost" unless
# given --host; Compose passes the service name, since that URL is what clients connect back to.

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src
COPY . .
RUN dotnet publish tools/OpcUaSimulator/ScadaDarbox.Tools.OpcUaSimulator.csproj --configuration Release --output /out --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0.11
WORKDIR /app
COPY --from=build /out ./
# The server creates its certificate on first start, beside the binaries.
RUN mkdir simulator-pki && chown "$APP_UID" simulator-pki
USER $APP_UID
EXPOSE 4840
ENTRYPOINT ["dotnet", "ScadaDarbox.Tools.OpcUaSimulator.dll"]
