// Azure-Infrastruktur der Live-Demo von OPCAgentSample (Resource-Group-Scope).
//
// Zwei Teile (Entscheidung 2026-10-16: Oberfläche immer sofort da, Backend nur bei Bedarf):
//   Static Web App (Free)  Weboberfläche und Momentaufnahme (Vorschau-Daten) auf https://opc.vompa.dev, kostet nichts, schläft nie
//   Container App          Backend auf https://api.opc.vompa.dev, schläft im Leerlauf (Scale-to-zero) und wird erst nach der Anmeldung geweckt
//
// Die Container App hat drei Container, die sich Netzwerk (localhost) und Lebenszyklus teilen:
//   server     ASP.NET Core mit API, MCP und Claude-Anbindung (einziger Zugang von außen, Port 8080; liefert keine Oberfläche aus)
//   simulator  OPC-UA-Server der simulierten Halle (opc.tcp://localhost:4840)
//   redis      Redis 7 als einziger Speicher: aktueller Zustand und verdichtete Zeitbereiche, flüchtig, ohne Persistenz
//              (Daten entstehen beim Start reproduzierbar aus Seed und Historie)
// Die Reports sind eine eigene Seite der Weboberfläche (GET /api/reports/summary); der OTLP-Export ist standardmäßig aus.
// Scale-to-zero: Ohne Anfragen schläft die App und kostet nichts; der erste Aufruf danach startet alles neu (Kaltstart).
// Die Images liegen öffentlich auf ghcr.io (keine Registry in Azure). Der Workflow .github/workflows/deploy-azure.yml
// baut sie und rollt diese Datei aus.

targetScope = 'resourceGroup'

@description('Präfix für die Ressourcennamen; zugleich Name der Container App.')
param name string = 'opc-agent'

@description('Region. Standard: die Region der Resource Group.')
param location string = resourceGroup().location

@description('Image-Präfix ohne Komponente und Tag, z. B. ghcr.io/vompa/opcagentsample (nur Kleinbuchstaben).')
param imagePrefix string = 'ghcr.io/vompa/opcagentsample'

@description('Tag der selbst gebauten Images (server, simulator), z. B. der Commit-SHA.')
param imageTag string

@description('Tag des gespiegelten Redis-Images (siehe Workflow).')
param redisTag string = '7'

@description('Mindestzahl Replikate. 0 = Scale-to-zero (Standard, kostet im Leerlauf nichts, Kaltstart beim ersten Aufruf). 1 = immer an (ca. 118 USD/Monat, siehe infra/README.md).')
@minValue(0)
@maxValue(1)
param minReplicas int = 0

@description('Wartezeit in Sekunden, bis die App ohne Anfragen auf null skaliert (KEDA cooldownPeriod). 3600 = eine Stunde Nachlauf: Wer die Demo nach der Anmeldung nutzt, hat sie eine Stunde nach der letzten Anfrage noch warm.')
@minValue(300)
@maxValue(7200)
param cooldownSeconds int = 3600

@description('Wach-Zeitfenster (KEDA-Cron) einschalten: Im Fenster bleibt 1 Replik auch ohne Anfragen wach (ca. 49 USD/Monat bei 8-18 Uhr). Standard aus: Die Demo schläft und wird nach der Anmeldung geweckt.')
param awakeWindow bool = false

@description('Beginn des Wach-Zeitfensters (Cron-Ausdruck), täglich bis awakeEnd in awakeTimeZone. Wirkt nur mit awakeWindow = true.')
param awakeStart string = '0 8 * * *'

@description('Ende des Wach-Zeitfensters (Cron-Ausdruck).')
param awakeEnd string = '0 18 * * *'

@description('Zeitzone des Wach-Zeitfensters (IANA).')
param awakeTimeZone string = 'Europe/Vienna'

@description('Eigene Domain des Backends (API), z. B. api.opc.vompa.dev. Leer = nur die Azure-Adresse. Voraussetzung: CNAME und asuid-TXT im DNS sowie ein verwaltetes Zertifikat (siehe infra/README.md).')
param customDomain string = ''

@description('Name des verwalteten Zertifikats in der Container-Apps-Umgebung. Pflicht, wenn customDomain gesetzt ist.')
param customDomainCertificate string = ''

@description('Region der Static Web App. Sie bietet nur westus2, centralus, eastus2, westeurope und eastasia an; die Inhalte liegen weltweit im CDN, die Region betrifft nur die Verwaltung. westeurope nimmt im Abo keine neuen Kunden an (RequestDisallowedByAzure), daher centralus.')
@allowed(['westus2', 'centralus', 'eastus2', 'westeurope', 'eastasia'])
param webLocation string = 'centralus'

@description('Eigene Domain der Oberfläche, z. B. opc.vompa.dev. Leer = nur die Azure-Adresse (*.azurestaticapps.net). Voraussetzung: CNAME opc auf die Static-Web-App-Adresse im DNS (siehe infra/README.md); ohne ihn schlägt das Deployment der Domain fehl.')
param webCustomDomain string = ''

@description('Anthropic-API-Key. Leer = Claude ist nicht angebunden, die Demo beantwortet nur die vorbereiteten Fragen (gekennzeichnet).')
@secure()
param claudeApiKey string = ''

@description('Claude-Modell der Demo.')
param claudeModel string = 'claude-haiku-5-5'

@description('Fragen an Claude je Browser-Sitzung und Rolle und Tag.')
param dailyQuestionsPerOwner int = 20

@description('Fragen an Claude insgesamt je Tag. Achtung: Der Zähler liegt im flüchtigen Redis und beginnt nach jedem Kaltstart neu; die harte Grenze ist das Ausgabenlimit in der Anthropic-Konsole.')
param dailyQuestionsTotal int = 150

@description('SHA-256 (64 Hex-Zeichen) des Zugangscodes.')
@secure()
param demoAccessCodeSha256 string

@description('Benannte Einladungs-Keys als ein String: name=sha256hex;name2=sha256hex (nur Hashes). Leer = keine Einladungslinks.')
@secure()
param demoInviteCodes string = ''

@description('Schlüssel (mind. 32 Zeichen) zum Signieren des Sitzungs-Cookies; bleibt über Deployments gleich, damit Anmeldungen halten.')
@secure()
param demoCookieKey string

@description('Höchstdauer eines Live-Streams (SSE), danach pausiert er bis zur nächsten Benutzeraktion. Sonst hielte ein offener Tab die App wach.')
param liveMaxStreamDuration string = '00:15:00'

var tags = { app: 'opc-agent-sample' }
var webName = '${name}-web'

// Oberfläche: Free-Tarif, ohne Funktionen und ohne Datenbank. Der Inhalt kommt per Deployment-Token aus dem Workflow
// (az staticwebapp secrets list), nicht aus dieser Datei.
resource web 'Microsoft.Web/staticSites@2023-12-01' = {
  name: webName
  location: webLocation
  tags: tags
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

resource webDomain 'Microsoft.Web/staticSites/customDomains@2023-12-01' = if (!empty(webCustomDomain)) {
  parent: web
  name: empty(webCustomDomain) ? 'unused.invalid' : webCustomDomain
  properties: { validationMethod: 'cname-delegation' }
}

// Herkunft der Oberfläche (für CORS des Backends): eigene Domain oder Azure-Adresse
var webHost = empty(webCustomDomain) ? web.properties.defaultHostname : webCustomDomain

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // Kostenbremse: höchstens 0,2 GB pro Tag aufnehmen (die ersten 5 GB im Monat sind frei)
    workspaceCapping: { dailyQuotaGb: json('0.2') }
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${name}-env'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource domainCertificate 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' existing = if (!empty(customDomain)) {
  parent: environment
  name: customDomainCertificate
}

// Öffentliche Adresse: eigene Domain oder die Azure-Adresse der App.
var publicHost = empty(customDomain) ? '${name}.${environment.properties.defaultDomain}' : customDomain

var serverEnv = concat(
  [
    { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
    // Der Ingress ist der einzige Zugang: Schema (Secure-Cookie) und Client-IP (Login-Drossel) aus X-Forwarded-*
    { name: 'ForwardedHeaders__Enabled', value: 'true' }
    { name: 'ConnectionStrings__Redis', value: 'localhost:6379' }
    // Die Container starten gleichzeitig: Redis braucht beim ersten Start einen Augenblick
    { name: 'Redis__StartupAttempts', value: '60' }
    { name: 'Opc__Endpoint', value: 'opc.tcp://localhost:4840' }
    { name: 'Live__MaxStreamDuration', value: liveMaxStreamDuration }
    // Reine API: Die Oberfläche liegt auf der Static Web App; CORS nur für deren Herkunft (mit Cookie, SameSite=Strict bleibt, da dieselbe Site vompa.dev)
    { name: 'Web__ServeUi', value: 'false' }
    { name: 'Cors__AllowedOrigins__0', value: 'https://${webHost}' }
    { name: 'Demo__Enabled', value: 'true' }
    { name: 'Demo__AccessCodeSha256', secretRef: 'demo-access-code-sha256' }
    { name: 'Demo__CookieKey', secretRef: 'demo-cookie-key' }
    { name: 'Claude__Model', value: claudeModel }
    { name: 'Claude__DailyQuestionsPerOwner', value: string(dailyQuestionsPerOwner) }
    { name: 'Claude__DailyQuestionsTotal', value: string(dailyQuestionsTotal) }
  ],
  empty(claudeApiKey) ? [] : [{ name: 'Claude__ApiKey', secretRef: 'claude-api-key' }],
  empty(demoInviteCodes) ? [] : [{ name: 'Demo__InviteCodes', secretRef: 'demo-invite-codes' }]
)

// 2025-01-01 (GA) statt 2024-03-01: erst ab dieser Version kennt der Scale-Block cooldownPeriod.
resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        customDomains: empty(customDomain) ? [] : [
          {
            name: customDomain
            certificateId: domainCertificate.id
            bindingType: 'SniEnabled'
          }
        ]
      }
      secrets: concat(
        [
          { name: 'demo-access-code-sha256', value: demoAccessCodeSha256 }
          { name: 'demo-cookie-key', value: demoCookieKey }
        ],
        empty(claudeApiKey) ? [] : [{ name: 'claude-api-key', value: claudeApiKey }],
        empty(demoInviteCodes) ? [] : [{ name: 'demo-invite-codes', value: demoInviteCodes }]
      )
    }
    template: {
      // Summe 1,25 vCPU / 2,5 GiB (zulässige Kombination im Consumption-Plan)
      containers: [
        {
          name: 'server'
          image: '${imagePrefix}-server:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: serverEnv
          probes: [
            {
              // Kaltstart: Die Historie wird aus dem Simulator geholt und verdichtet; bis zu 5 Minuten Geduld
              type: 'Startup'
              httpGet: { path: '/api/health', port: 8080 }
              periodSeconds: 5
              failureThreshold: 60
            }
            {
              type: 'Liveness'
              httpGet: { path: '/api/health', port: 8080 }
              periodSeconds: 30
              failureThreshold: 3
            }
          ]
        }
        {
          name: 'simulator'
          image: '${imagePrefix}-simulator:${imageTag}'
          // Gemessen ca. 440 MiB (Historie im Speicher), deshalb 1 GiB
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            // Nur innerhalb der App erreichbar (kein Ingress auf 4840)
            { name: 'Simulator__BindAddress', value: '0.0.0.0' }
          ]
        }
        {
          name: 'redis'
          image: '${imagePrefix}-redis:${redisTag}'
          // Einziger Speicher: keine Snapshots, kein AOF, Speicher begrenzt, nie verdrängen (noeviction: Verdrängen würde Daten vernichten)
          command: ['redis-server']
          args: ['--save', '', '--appendonly', 'no', '--maxmemory', '300mb', '--maxmemory-policy', 'noeviction']
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
        }
      ]
      scale: {
        minReplicas: minReplicas
        // Nachlauf vor dem Einschlafen (Standard eine Stunde)
        cooldownPeriod: cooldownSeconds
        // Genau eine Replik: Redis liegt in der App, jede weitere Replik hätte eigene Daten
        maxReplicas: 1
        rules: concat(
          [
            {
              name: 'http'
              http: { metadata: { concurrentRequests: '50' } }
            }
          ],
          // Bürozeiten (nur mit awakeWindow): im Zeitfenster bleibt die App ohne Anfragen wach (keine Wartezeit beim ersten Aufruf)
          !awakeWindow || empty(awakeStart) ? [] : [
            {
              name: 'buerozeiten'
              custom: {
                type: 'cron'
                metadata: {
                  timezone: awakeTimeZone
                  start: awakeStart
                  end: awakeEnd
                  desiredReplicas: '1'
                }
              }
            }
          ]
        )
      }
    }
  }
}

output appFqdn string = app.properties.configuration.ingress.fqdn
output webName string = web.name
output webDefaultHost string = web.properties.defaultHostname
output webHost string = webHost
output publicHost string = publicHost
output customDomainVerificationId string = app.properties.customDomainVerificationId
