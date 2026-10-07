# Build context is the repository root:  docker build -f src/Tcp.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Tcp.Domain/Tcp.Domain.csproj src/Tcp.Domain/
COPY src/Tcp.Infrastructure/Tcp.Infrastructure.csproj src/Tcp.Infrastructure/
COPY src/Tcp.Api/Tcp.Api.csproj src/Tcp.Api/
RUN dotnet restore src/Tcp.Api/Tcp.Api.csproj
COPY src/ src/
# Seed task definitions are linked into the app from the spec folder (single source of truth).
COPY specs/004-task-provider-spi/seed/ specs/004-task-provider-spi/seed/
RUN dotnet publish src/Tcp.Api/Tcp.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# Chiseled = distroless, non-root (user "app"), no shell. "extra" adds ICU + tzdata.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_gcServer=0
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Tcp.Api.dll"]
