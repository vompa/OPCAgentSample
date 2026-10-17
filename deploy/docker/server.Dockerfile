# Server mit Weboberfläche. Build-Kontext ist das Repository-Root:
#   docker build -f deploy/docker/server.Dockerfile -t opcagentsample-server .

# Stufe 1: Weboberfläche (Vue 3 + TypeScript, Vite) nach src/OPCAgentSample.Server/wwwroot bauen
FROM node:22-bookworm-slim AS web
WORKDIR /src/src/OPCAgentSample.Web
COPY src/OPCAgentSample.Web/package.json src/OPCAgentSample.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/OPCAgentSample.Web/ ./
# Das Image dient der Live-Demo: Der Zugang per Zugangscode ist beim Bauen bekannt, die Oberfläche fragt den Server nicht danach.
ARG VITE_DEMO_ACCESS=true
ENV VITE_DEMO_ACCESS=$VITE_DEMO_ACCESS
RUN npm run build

# Stufe 2: .NET-Server veröffentlichen
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Erst nur die Projektdateien, damit der Restore-Layer im Cache bleibt
COPY Directory.Build.props .editorconfig ./
COPY src/OPCAgentSample.Domain/OPCAgentSample.Domain.csproj src/OPCAgentSample.Domain/
COPY src/OPCAgentSample.Kpi/OPCAgentSample.Kpi.csproj src/OPCAgentSample.Kpi/
COPY src/OPCAgentSample.Rollup/OPCAgentSample.Rollup.csproj src/OPCAgentSample.Rollup/
COPY src/OPCAgentSample.Simulation/OPCAgentSample.Simulation.csproj src/OPCAgentSample.Simulation/
COPY src/OPCAgentSample.Server/OPCAgentSample.Server.csproj src/OPCAgentSample.Server/
RUN dotnet restore src/OPCAgentSample.Server/OPCAgentSample.Server.csproj
COPY src/ src/
COPY --from=web /src/src/OPCAgentSample.Server/wwwroot/ src/OPCAgentSample.Server/wwwroot/
RUN dotnet publish src/OPCAgentSample.Server/OPCAgentSample.Server.csproj -c Release --no-restore -o /app

# Stufe 3: Laufzeit
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# Nicht als root laufen (das Image bringt den Benutzer "app" mit). Zertifikate des OPC-Clients liegen im
# beschreibbaren, flüchtigen /tmp.
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Opc__PkiPath=/tmp/pki/collector
EXPOSE 8080
ENTRYPOINT ["dotnet", "OPCAgentSample.Server.dll"]
