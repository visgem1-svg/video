# Build context: services/dotnet   (docker build -f Executor/Dockerfile services/dotnet)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY Executor/Executor.csproj Executor/
RUN dotnet restore Executor/Executor.csproj
COPY Executor/ Executor/
RUN dotnet publish Executor/Executor.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
RUN apk add --no-cache libsasl zstd-libs   # runtime deps of the bundled alpine librdkafka
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5003     DOTNET_gcServer=0     DOTNET_GCHeapHardLimit=0x6000000     DOTNET_TieredPGO=0     DOTNET_EnableDiagnostics=0     DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
USER app
EXPOSE 5003
ENTRYPOINT ["dotnet", "Executor.dll"]
