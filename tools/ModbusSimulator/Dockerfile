# The Modbus TCP simulator the Phase 6 gate reads from. Not part of the product.
# It listens on loopback unless given --listen; Compose passes that explicitly, so the
# default stays the same for everyone running it locally.

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src
COPY . .
RUN dotnet publish tools/ModbusSimulator/ScadaDarbox.Tools.ModbusSimulator.csproj --configuration Release --output /out --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0.11
WORKDIR /app
COPY --from=build /out ./
USER $APP_UID
EXPOSE 5502
ENTRYPOINT ["dotnet", "ScadaDarbox.Tools.ModbusSimulator.dll"]
