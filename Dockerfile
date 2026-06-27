# syntax=docker/dockerfile:1

# ---- Build stage --------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer-cached on project/solution changes only).
COPY Xental.slnx ./
COPY src/Xental.Domain/Xental.Domain.csproj                 src/Xental.Domain/
COPY src/Xental.Application/Xental.Application.csproj        src/Xental.Application/
COPY src/Xental.Infrastructure/Xental.Infrastructure.csproj src/Xental.Infrastructure/
COPY src/Xental.Api/Xental.Api.csproj                        src/Xental.Api/
RUN dotnet restore Xental.slnx

# Copy the rest and publish.
COPY . .
RUN dotnet publish src/Xental.Api/Xental.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- Runtime stage ------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Listen on 8080 (the default non-root port for the .NET runtime images).
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080 \
    LOG_DIRECTORY=/app/logs

# Create the log directory owned by the non-root "app" user (UID 1654, shipped
# in the .NET images). A named volume mounted here inherits this ownership, so
# the non-root process can write logs.
RUN mkdir -p /app/logs && chown -R app:app /app/logs

COPY --from=build --chown=app:app /app/publish .

# Run as the non-root user baked into the base image.
USER app

ENTRYPOINT ["dotnet", "Xental.Api.dll"]
