# OPC-UA-Simulator. Build-Kontext ist das Repository-Root:
#   docker build -f deploy/docker/simulator.Dockerfile -t opcagentsample-simulator .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig ./
COPY src/OPCAgentSample.Domain/OPCAgentSample.Domain.csproj src/OPCAgentSample.Domain/
COPY src/OPCAgentSample.Kpi/OPCAgentSample.Kpi.csproj src/OPCAgentSample.Kpi/
COPY src/OPCAgentSample.Simulation/OPCAgentSample.Simulation.csproj src/OPCAgentSample.Simulation/
COPY src/OPCAgentSample.Simulator/OPCAgentSample.Simulator.csproj src/OPCAgentSample.Simulator/
RUN dotnet restore src/OPCAgentSample.Simulator/OPCAgentSample.Simulator.csproj
COPY src/ src/
RUN dotnet publish src/OPCAgentSample.Simulator/OPCAgentSample.Simulator.csproj -c Release --no-restore -o /app

# Laufzeit: Konsolenanwendung, braucht nur die .NET-Runtime
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER app
# Im Container an alle Schnittstellen binden (lokal ist der Standard 127.0.0.1). In der Azure Container App teilen sich
# alle Container das Netzwerk; der Port 4840 ist dort nicht von außen erreichbar (Ingress nur auf den Server).
ENV Simulator__BindAddress=0.0.0.0 \
    Simulator__PkiPath=/tmp/pki/simulator
EXPOSE 4840
ENTRYPOINT ["dotnet", "OPCAgentSample.Simulator.dll"]
