FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/SeatRes.Api/SeatRes.Api.csproj src/SeatRes.Api/
RUN dotnet restore src/SeatRes.Api/SeatRes.Api.csproj
COPY src/ src/
RUN dotnet publish src/SeatRes.Api/SeatRes.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
# The runtime image has no curl; the app probes its own readiness endpoint.
HEALTHCHECK --interval=10s --timeout=4s --start-period=20s --retries=3 CMD ["dotnet", "SeatRes.Api.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "SeatRes.Api.dll"]
