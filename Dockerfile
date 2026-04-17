FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Luca.Registry.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Render provides PORT env var; default to 8080 for local docker run
ENV PORT=8080
EXPOSE 8080

# SQLite DB lives in /app/data — ephemeral on Render free tier
ENV LUCA_DB=/app/data/registry.db
VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "Luca.Registry.dll"]
