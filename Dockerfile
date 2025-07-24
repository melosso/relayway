
# ----------- Base Runtime Image -----------
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base
USER $APP_UID
WORKDIR /app

# ----------- Build Stage -----------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Copy project file and restore dependencies
COPY ["Relayway.csproj", "./"]
RUN dotnet restore "Relayway.csproj"
# Copy all source files
COPY . .
# Build the project
RUN dotnet build "Relayway.csproj" -c $BUILD_CONFIGURATION -o /app/build

# ----------- Publish Stage -----------
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Relayway.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# ----------- Final Runtime Image -----------
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Set entrypoint
ENTRYPOINT ["dotnet", "Relayway.dll"]
