FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Option.Data.Scheduler/Option.Data.Scheduler.csproj", "Option.Data.Scheduler/"]
RUN dotnet restore "Option.Data.Scheduler/Option.Data.Scheduler.csproj"
COPY . .
WORKDIR "/src/Option.Data.Scheduler"
RUN dotnet build "./Option.Data.Scheduler.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Option.Data.Scheduler.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Option.Data.Scheduler.dll"]
