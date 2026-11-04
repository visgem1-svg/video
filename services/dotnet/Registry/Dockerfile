# build context: services/dotnet  (docker build -f Registry/Dockerfile services/dotnet)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY Registry/Registry.csproj Registry/
RUN dotnet restore Registry/Registry.csproj
COPY Registry/ Registry/
RUN dotnet publish Registry/Registry.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5001     DOTNET_gcServer=0     DOTNET_GCHeapHardLimit=0x6000000     DOTNET_TieredPGO=0     DOTNET_EnableDiagnostics=0     DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 5001
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 CMD wget -qO- http://127.0.0.1:5001/health || exit 1
ENTRYPOINT ["dotnet", "Registry.dll"]
