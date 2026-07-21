# Multi-stage build for all .NET game services
# Build stage: compiles all projects from the solution
# Runtime stages: one per service, copies only the needed binaries

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY Game.sln Directory.Build.props Directory.Packages.props ./
COPY src/Game.Contracts/Game.Contracts.csproj src/Game.Contracts/
COPY src/Game.Persistence/Game.Persistence.csproj src/Game.Persistence/
COPY src/Game.ServiceDefaults/Game.ServiceDefaults.csproj src/Game.ServiceDefaults/
COPY src/Game.Gateway/Game.Gateway.csproj src/Game.Gateway/
COPY src/Game.Simulation/Game.Simulation.csproj src/Game.Simulation/
COPY src/Game.EventLog/Game.EventLog.csproj src/Game.EventLog/
COPY src/Game.Progression/Game.Progression.csproj src/Game.Progression/
COPY src/Game.OperatorApi/Game.OperatorApi.csproj src/Game.OperatorApi/

# Restore only the src projects (tests excluded from Docker build)
RUN dotnet restore src/Game.Gateway \
 && dotnet restore src/Game.Simulation \
 && dotnet restore src/Game.EventLog \
 && dotnet restore src/Game.Progression \
 && dotnet restore src/Game.OperatorApi

# Copy everything and publish each service
COPY src/ src/
RUN dotnet publish src/Game.Simulation -c Release -o /app/simulation --no-restore
RUN dotnet publish src/Game.EventLog -c Release -o /app/eventlog --no-restore
RUN dotnet publish src/Game.Progression -c Release -o /app/progression --no-restore
RUN dotnet publish src/Game.OperatorApi -c Release -o /app/operatorapi --no-restore

# --- Gateway release identity (M1 risk 9) -------------------------------------------------------
# The Gateway bakes the mod release it admits into its own assembly, so the expected value travels
# with the artifact and cannot drift from it. That only works if the value reaches THIS publish:
# the image is what ships, and Game.Gateway.csproj defaults the property to "dev", which
# ValheimReleaseIdentity maps to null — a gate that is disabled while looking armed. A publish here
# that omits the property is exactly the defect this argument exists to close, and it is silent.
#
# Declared after the other services so changing the release id rebuilds only the Gateway layer.
ARG LUMBERJACKS_EXPECTED_MOD_RELEASE=dev
ARG LUMBERJACKS_REQUIRE_RELEASE=0

# A promotable build must not be able to ship the sentinel. Local and CI builds leave
# LUMBERJACKS_REQUIRE_RELEASE at 0 and keep the "dev" default, which disables the gate on purpose;
# a release build sets it to 1 and fails closed on an absent, sentinel, or malformed id. The pattern
# is the same one New-ReleaseCut.ps1 enforces, because this string becomes an artifact's identity.
RUN set -eu; \
    if [ "$LUMBERJACKS_REQUIRE_RELEASE" = "1" ]; then \
      case "$LUMBERJACKS_EXPECTED_MOD_RELEASE" in \
        ''|dev) \
          echo "ERROR: a promotable Gateway build requires a real LUMBERJACKS_EXPECTED_MOD_RELEASE;" >&2; \
          echo "       got '$LUMBERJACKS_EXPECTED_MOD_RELEASE'." >&2; \
          exit 1 ;; \
      esac; \
      if ! echo "$LUMBERJACKS_EXPECTED_MOD_RELEASE" \
           | grep -Eq '^m[0-9]+-[a-z0-9]+-[0-9]{8}-r[0-9]+$'; then \
        echo "ERROR: '$LUMBERJACKS_EXPECTED_MOD_RELEASE' is not <milestone>-<label>-<yyyymmdd>-r<n>." >&2; \
        exit 1; \
      fi; \
      echo "promotable Gateway build; admitted mod release: $LUMBERJACKS_EXPECTED_MOD_RELEASE"; \
    fi

RUN dotnet publish src/Game.Gateway -c Release -o /app/gateway --no-restore \
      -p:LumberjacksExpectedModRelease="$LUMBERJACKS_EXPECTED_MOD_RELEASE"

# --- Runtime images ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS gateway
WORKDIR /app
COPY --from=build /app/gateway .
ENV ASPNETCORE_URLS=http://+:4000
EXPOSE 4000
ENTRYPOINT ["dotnet", "Game.Gateway.dll"]

# Simulation runs in-process inside Gateway in production.
# This standalone target is kept for isolated HTTP-only testing.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS simulation
WORKDIR /app
COPY --from=build /app/simulation .
ENV ASPNETCORE_URLS=http://+:4001
EXPOSE 4001
ENTRYPOINT ["dotnet", "Game.Simulation.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS eventlog
WORKDIR /app
COPY --from=build /app/eventlog .
ENV ASPNETCORE_URLS=http://+:4002
EXPOSE 4002
ENTRYPOINT ["dotnet", "Game.EventLog.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS progression
WORKDIR /app
COPY --from=build /app/progression .
ENV ASPNETCORE_URLS=http://+:4003
EXPOSE 4003
ENTRYPOINT ["dotnet", "Game.Progression.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS operatorapi
WORKDIR /app
COPY --from=build /app/operatorapi .
ENV ASPNETCORE_URLS=http://+:4004
EXPOSE 4004
ENTRYPOINT ["dotnet", "Game.OperatorApi.dll"]
