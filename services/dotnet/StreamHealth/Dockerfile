# build context: services/dotnet  (docker build -f StreamHealth/Dockerfile services/dotnet)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY StreamHealth/StreamHealth.csproj StreamHealth/
RUN dotnet restore StreamHealth/StreamHealth.csproj
COPY StreamHealth/ StreamHealth/
RUN dotnet publish StreamHealth/StreamHealth.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5002     DOTNET_gcServer=0     DOTNET_GCHeapHardLimit=0x6000000     DOTNET_TieredPGO=0     DOTNET_EnableDiagnostics=0     DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 5002
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 CMD wget -qO- http://127.0.0.1:5002/health || exit 1
ENTRYPOINT ["dotnet", "StreamHealth.dll"]
