# Build context: services/dotnet   (docker build -f RealtimeGateway/Dockerfile services/dotnet)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY RealtimeGateway/RealtimeGateway.csproj RealtimeGateway/
RUN dotnet restore RealtimeGateway/RealtimeGateway.csproj
COPY RealtimeGateway/ RealtimeGateway/
RUN dotnet publish RealtimeGateway/RealtimeGateway.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
RUN apk add --no-cache libsasl zstd-libs   # runtime deps of the bundled alpine librdkafka
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5005     DOTNET_gcServer=0     DOTNET_GCHeapHardLimit=0x6000000     DOTNET_TieredPGO=0     DOTNET_EnableDiagnostics=0     DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
USER app
EXPOSE 5005
ENTRYPOINT ["dotnet", "RealtimeGateway.dll"]
