// Not-Stopp der Live-Demo bei Budgetüberschreitung (Resource-Group-Scope, einmalig von Hand ausrollen).
//
// Azure kennt für Pay-As-You-Go keine harte Ausgabengrenze. Stattdessen löst das Monatsbudget des Abos bei 100 % der
// tatsächlichen Kosten diese Aktionsgruppe aus; die Logic App stoppt daraufhin die Container App (POST …/stop).
// Wieder einschalten: siehe infra/README.md („Not-Stopp“).
//
// Ausrollen (braucht Rechte zum Anlegen von Rollenzuweisungen, z. B. Besitzer; der Deploy-Workflow hat sie bewusst nicht):
//   az deployment group create -g rg-opc-agent --template-file infra/cost-guard.bicep
// Danach die Aktionsgruppe (Ausgabe actionGroupId) im Budget eintragen, siehe infra/README.md.

targetScope = 'resourceGroup'

@description('Name der Container App, die gestoppt wird.')
param appName string = 'opc-agent'

@description('Präfix der Ressourcennamen.')
param name string = 'opc-agent'

@description('Region der Logic App. Aktionsgruppen sind global.')
param location string = resourceGroup().location

var tags = { app: 'opc-agent-sample' }
var contributorRoleId = 'b24988ac-6180-42a0-ab88-20f7382dd24c'
var stopUri = '${environment().resourceManager}subscriptions/${subscription().subscriptionId}/resourceGroups/${resourceGroup().name}/providers/Microsoft.App/containerApps/${appName}/stop?api-version=2024-03-01'

resource stopApp 'Microsoft.Logic/workflows@2019-05-01' = {
  name: '${name}-stop'
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    state: 'Enabled'
    definition: {
      '$schema': 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#'
      contentVersion: '1.0.0.0'
      triggers: {
        // Aufruf durch die Aktionsgruppe (Budget-Alarm); der Inhalt wird nicht ausgewertet
        budgetAlert: {
          type: 'Request'
          kind: 'Http'
        }
      }
      actions: {
        stopContainerApp: {
          type: 'Http'
          inputs: {
            method: 'POST'
            uri: stopUri
            authentication: {
              type: 'ManagedServiceIdentity'
              audience: environment().resourceManager
            }
          }
        }
      }
    }
  }
}

// Die Logic App darf nur in dieser Resource Group handeln
resource stopAppRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, stopApp.id, contributorRoleId)
  properties: {
    principalId: stopApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
  }
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${name}-budget-stop'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'opcstop'
    enabled: true
    logicAppReceivers: [
      {
        name: 'stop-opc-demo'
        resourceId: stopApp.id
        callbackUrl: listCallbackUrl('${stopApp.id}/triggers/budgetAlert', '2019-05-01').value
        useCommonAlertSchema: true
      }
    ]
  }
}

output actionGroupId string = actionGroup.id
