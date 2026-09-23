# The OPC UA simulator. Not part of the product. It already listens on every interface, and
# the driver's client replaces the "localhost" in the endpoint URL it is sent with the name it
# reached the server by — so, unlike the Modbus simulator, it needs no option to be reachable
# from another container (checked from one, Phase 6).

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
