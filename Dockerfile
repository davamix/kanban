# syntax=docker/dockerfile:1

# --- Build stage ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached) using just the project file.
COPY src/KanbanApi/KanbanApi.csproj src/KanbanApi/
RUN dotnet restore src/KanbanApi/KanbanApi.csproj

# Copy the rest and publish a framework-dependent build.
COPY . .
RUN dotnet publish src/KanbanApi/KanbanApi.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# --- Runtime stage -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Npgsql probes for the Kerberos/GSSAPI library at connect time; without it the slim image
# logs a misleading "Error: cannot open libgssapi_krb5.so.2" before falling back. Install it
# so startup logs stay clean. (apt lists removed to keep the image small.)
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Kestrel listens on 8080 inside the container (default for the aspnet image).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Run as the image's built-in non-root user (resolves Trivy AVD-DS-0002).
USER $APP_UID

ENTRYPOINT ["dotnet", "KanbanApi.dll"]
