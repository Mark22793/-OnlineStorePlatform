# syntax=docker/dockerfile:1
# Multi-stage build for ApiGateway. Build context is the repository root (see docker-compose.yml),
# so the shared projects and contracts/ (for generated OpenAPI clients) are available.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
RUN dotnet restore "src/Gateway/ApiGateway/ApiGateway.csproj"
RUN dotnet publish "src/Gateway/ApiGateway/ApiGateway.csproj" -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# curl is only used by the compose health check
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "ApiGateway.dll"]
