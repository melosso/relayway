FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY Source/Directory.Build.props Source/Directory.Packages.props Source/
COPY Source/Relayway/Relayway.csproj Source/Relayway/
RUN dotnet restore Source/Relayway/Relayway.csproj
COPY Source/Relayway Source/Relayway
RUN dotnet publish Source/Relayway/Relayway.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV Smtp__Host=0.0.0.0 Smtp__Port=2525
EXPOSE 2525
ENTRYPOINT ["dotnet", "Relayway.dll"]
