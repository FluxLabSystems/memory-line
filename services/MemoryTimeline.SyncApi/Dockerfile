# Memory Line Sync API — multi-stage build (design §4.2 self-hosted mode).
#
# IMPORTANT: the build context is the REPO ROOT, because the project references
# the shared-contracts project outside this directory. Build with:
#
#   docker build -f services/MemoryTimeline.SyncApi/Dockerfile -t memory-line-sync .
#
# Run with a persistent volume for the data directory:
#
#   docker run -p 8080:8080 -v memory-line-sync-data:/data memory-line-sync

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first with only the project files so dependency layers cache.
COPY shared-contracts/dotnet/MemoryTimeline.SyncContracts/MemoryTimeline.SyncContracts.csproj shared-contracts/dotnet/MemoryTimeline.SyncContracts/
COPY services/MemoryTimeline.SyncApi/MemoryTimeline.SyncApi.csproj services/MemoryTimeline.SyncApi/
RUN dotnet restore services/MemoryTimeline.SyncApi/MemoryTimeline.SyncApi.csproj

# Then copy the sources (the shared-contracts project too — see the note above).
COPY shared-contracts/ shared-contracts/
COPY services/MemoryTimeline.SyncApi/ services/MemoryTimeline.SyncApi/
RUN dotnet publish services/MemoryTimeline.SyncApi/MemoryTimeline.SyncApi.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Keep all state (SQLite DB, artifacts, signing key, pairing code) on one volume.
ENV SyncApi__DataDir=/data
VOLUME /data

EXPOSE 8080
ENTRYPOINT ["dotnet", "MemoryTimeline.SyncApi.dll"]
