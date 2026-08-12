ARG LUMBERJACKS_SOURCE_REVISION=unknown

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY packages-local/ packages-local/
COPY src/Game.Companion/Game.Companion.csproj src/Game.Companion/
RUN dotnet restore src/Game.Companion/Game.Companion.csproj
COPY src/Game.Companion/ src/Game.Companion/
RUN dotnet publish src/Game.Companion/Game.Companion.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
ARG LUMBERJACKS_SOURCE_REVISION
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENV LUMBERJACKS_COMPANION_DATA=/data
ENV LUMBERJACKS_COMPANION_SOURCE_REVISION=${LUMBERJACKS_SOURCE_REVISION}
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lumberjacks.Companion.dll"]
