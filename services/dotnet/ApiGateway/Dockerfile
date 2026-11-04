# Build context: services/dotnet   (docker build -f ApiGateway/Dockerfile services/dotnet)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY ApiGateway/ApiGateway.csproj ApiGateway/
RUN dotnet restore ApiGateway/ApiGateway.csproj
COPY ApiGateway/ ApiGateway/
RUN dotnet publish ApiGateway/ApiGateway.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8000     DOTNET_gcServer=0     DOTNET_GCHeapHardLimit=0x6000000     DOTNET_TieredPGO=0     DOTNET_EnableDiagnostics=0     DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
USER app
EXPOSE 8000
ENTRYPOINT ["dotnet", "ApiGateway.dll"]
