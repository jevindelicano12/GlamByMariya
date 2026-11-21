# syntax=docker/dockerfile:1
# Multi-stage production image for the Glam by Mariya booking system (.NET 10).
# No secrets are baked in: every credential arrives as a runtime environment variable.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Backend/GlamByMariya.Core/GlamByMariya.Core.csproj Backend/GlamByMariya.Core/
COPY Backend/GlamByMariya.Infrastructure/GlamByMariya.Infrastructure.csproj Backend/GlamByMariya.Infrastructure/
COPY Frontend/GlamByMariya.Web/GlamByMariya.Web.csproj Frontend/GlamByMariya.Web/
RUN dotnet restore Frontend/GlamByMariya.Web/GlamByMariya.Web.csproj
# Limit source inputs; local tools, fixtures and deployment secrets are not build inputs.
COPY Backend/ Backend/
COPY Frontend/ Frontend/
# Ordered SQL migrations, published next to the app for --database-migrate.
COPY database/migrations/ database/migrations/
RUN dotnet publish Frontend/GlamByMariya.Web/GlamByMariya.Web.csproj -c Release -o /app/publish --no-restore \
    /p:DebugType=None /p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# Npgsql's GSS encryption/authentication native dependency is dynamically
# loaded by the Debian/Ubuntu .NET runtime image. Install the distro package
# rather than relying on incidental transitive packages in the SDK stage.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
# Explicitly copy the sole public CA certificate needed for TLS VerifyFull.
# Ignore rules exclude all other App_Data (including local backups/uploads).
RUN mkdir -p /app/App_Data/Certificates
COPY --from=build /src/Frontend/GlamByMariya.Web/App_Data/Certificates/supabase-prod-ca-2021.crt ./App_Data/Certificates/supabase-prod-ca-2021.crt
COPY --from=build /app/publish ./
# Render assigns $PORT at runtime; Program.cs binds to it automatically.
# Without $PORT (local `docker run`) Kestrel uses its default port.
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "GlamByMariya.Web.dll"]
