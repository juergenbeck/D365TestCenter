<#
.SYNOPSIS
    Deployt das D365TestCenter.CrmPlugin als PluginPackage (NuGet) nach Dataverse.

.DESCRIPTION
    Ersetzt die klassische Plugin-Assembly-Registrierung mit ILRepack durch das
    moderne PluginPackage-Format. Vorteile:
    - Kein Strong-Name-Signing noetig
    - Dependencies sauber getrennt (keine gemergeten DLLs)
    - Sandbox-Cache-Invalidierung funktioniert zuverlaessig per Web API

    Modi:
    - Install: Erste Installation. Legt Package, Custom API und Steps neu an.
    - Update:  Nur Package-Content aktualisieren. Steps bleiben unveraendert.

.PARAMETER Headers
    Authentifizierte Headers für Dataverse Web API (Authorization Bearer etc.).

.PARAMETER BaseUrl
    Dataverse Base-URL inkl. api/data/v9.2

.PARAMETER Mode
    Install (default Install) oder Update.

.PARAMETER SolutionName
    Solution-Name für AddSolutionComponent. Default: D365TestCenter.

.EXAMPLE
    . ./TokenVault.ps1
    $headers = Get-VaultHeaders -System 'dataverse_dev'
    ./Deploy-PluginPackage.ps1 -Headers $headers -BaseUrl "https://markant-dev.crm4.dynamics.com/api/data/v9.2" -Mode Install
#>

param(
    [Parameter(Mandatory)][hashtable]$Headers,
    [Parameter(Mandatory)][string]$BaseUrl,
    [ValidateSet('Install', 'Update')][string]$Mode = 'Install',
    [string]$SolutionName = 'D365TestCenter'
)

$ErrorActionPreference = 'Stop'

# ════════════════════════════════════════════════════════════════════
#  Helpers
# ════════════════════════════════════════════════════════════════════

function Invoke-DataverseApi {
    param(
        [string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Uri,
        [hashtable]$ExtraHeaders = @{},
        [string]$Body
    )
    $h = @{}
    foreach ($k in $Headers.Keys) { $h[$k] = $Headers[$k] }
    foreach ($k in $ExtraHeaders.Keys) { $h[$k] = $ExtraHeaders[$k] }

    if ($Method -eq 'GET') {
        return Invoke-RestMethod -Uri $Uri -Headers $h -Method Get
    }

    # POST/PATCH/DELETE mit Body als UTF-8 String
    $params = @{ Uri = $Uri; Headers = $h; Method = $Method; UseBasicParsing = $true }
    if ($Body) {
        $params.Body = [System.Text.Encoding]::UTF8.GetBytes($Body)
        $params.ContentType = 'application/json; charset=utf-8'
    }
    $response = Invoke-WebRequest @params
    if ($response.Content) {
        try {
            return $response.Content | ConvertFrom-Json
        } catch {
            return $response.Content
        }
    }
    return $null
}

# ════════════════════════════════════════════════════════════════════
#  1. Paket finden
# ════════════════════════════════════════════════════════════════════

$packageDir = Join-Path $PSScriptRoot '..\backend\bin\Release\outputPackages'
if (-not (Test-Path $packageDir)) {
    throw "Package-Verzeichnis nicht gefunden: $packageDir. Vorher 'dotnet build -c Release' ausführen."
}

$packageFile = Get-ChildItem -Path $packageDir -Filter 'jbe_D365TestCenter.*.nupkg' |
Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $packageFile) {
    throw "Kein PluginPackage gefunden in $packageDir. Vorher 'dotnet build -c Release' ausführen."
}

$packageVersion = [regex]::Match($packageFile.Name, 'jbe_D365TestCenter\.(.+)\.nupkg').Groups[1].Value
Write-Host "Package: $($packageFile.Name) (Version $packageVersion, $([math]::Round($packageFile.Length/1KB))KB)"

$packageContent = [Convert]::ToBase64String([IO.File]::ReadAllBytes($packageFile.FullName))

# ════════════════════════════════════════════════════════════════════
#  2. Package registrieren (POST oder PATCH)
# ════════════════════════════════════════════════════════════════════

$existingUri = "$BaseUrl/pluginpackages?" + '$filter=uniquename eq ' + "'jbe_D365TestCenter'" + '&$select=pluginpackageid,version'
$existing = Invoke-DataverseApi -Uri $existingUri

if ($existing.value.Count -gt 0) {
    $packageId = $existing.value[0].pluginpackageid
    Write-Host "Existing package found: $packageId (old version $($existing.value[0].version))"
    Write-Host "Updating package content..."

    $patchBody = @{
        content = $packageContent
        version = $packageVersion
    } | ConvertTo-Json

    Invoke-DataverseApi -Method Patch -Uri "$BaseUrl/pluginpackages($packageId)" `
        -ExtraHeaders @{ 'If-Match' = '*' } -Body $patchBody
    Write-Host "Package content updated. Waiting for sandbox cache invalidation..."
    Start-Sleep -Seconds 10
} else {
    Write-Host "Creating new package..."
    $postBody = @{
        name       = 'jbe_D365TestCenter'
        uniquename = 'jbe_D365TestCenter'
        version    = $packageVersion
        content    = $packageContent
    } | ConvertTo-Json

    $result = Invoke-DataverseApi -Method Post -Uri "$BaseUrl/pluginpackages" `
        -ExtraHeaders @{ 'MSCRM.SolutionName' = $SolutionName; 'Prefer' = 'return=representation' } `
        -Body $postBody
    $packageId = $result.pluginpackageid
    Write-Host "Package registered: $packageId"
}

# ════════════════════════════════════════════════════════════════════
#  3. Warten auf Type-Extraktion
# ════════════════════════════════════════════════════════════════════

Write-Host "Waiting for plugin type extraction..."
$typeId = $null
for ($i = 1; $i -le 20; $i++) {
    Start-Sleep -Seconds 3
    $typesUri = "$BaseUrl/plugintypes?" + '$filter=typename eq ' + "'D365TestCenter.CrmPlugin.RunTestsOnStatusChange'" + '&$select=plugintypeid,_pluginassemblyid_value'
    $types = (Invoke-DataverseApi -Uri $typesUri).value
    if ($types.Count -gt 0) {
        $typeId = $types[0].plugintypeid
        $asmIdOfType = $types[0]._pluginassemblyid_value
        Write-Host "PluginType found after $($i*3)s: $typeId (assembly: $asmIdOfType)"
        break
    }
    Write-Host "  ... $($i*3)s"
}
if (-not $typeId) { throw "PluginType 'D365TestCenter.CrmPlugin.RunTestsOnStatusChange' nicht extrahiert nach 60s." }

$customApiTypeId = $null
$apiTypeUri = "$BaseUrl/plugintypes?" + '$filter=typename eq ' + "'D365TestCenter.CrmPlugin.RunIntegrationTestsApi'" + '&$select=plugintypeid'
$apiTypes = (Invoke-DataverseApi -Uri $apiTypeUri).value
if ($apiTypes.Count -gt 0) {
    $customApiTypeId = $apiTypes[0].plugintypeid
    Write-Host "Custom API Type found: $customApiTypeId"
}

# ════════════════════════════════════════════════════════════════════
#  4. Mode Update: Nur Steps auf neuen PluginType umhaengen
# ════════════════════════════════════════════════════════════════════

if ($Mode -eq 'Update') {
    Write-Host ""
    Write-Host "=== Mode: Update (Steps auf neuen PluginType umhaengen) ==="

    $stepsUri = "$BaseUrl/sdkmessageprocessingsteps?" + '$filter=contains(name,' + "'D365TestCenter.RunTestsOnStatusChange'" + ')'
    $existingSteps = (Invoke-DataverseApi -Uri $stepsUri).value
    Write-Host "Found $($existingSteps.Count) existing steps"

    foreach ($s in $existingSteps) {
        if ($s._plugintypeid_value -ne $typeId) {
            Write-Host "  Step '$($s.name)' belongs to old PluginType, updating..."
            $patchBody = @{ "plugintypeid@odata.bind" = "/plugintypes($typeId)" } | ConvertTo-Json
            Invoke-DataverseApi -Method Patch -Uri "$BaseUrl/sdkmessageprocessingsteps($($s.sdkmessageprocessingstepid))" `
                -ExtraHeaders @{ 'If-Match' = '*' } -Body $patchBody
            Write-Host "    OK"
        } else {
            Write-Host "  Step '$($s.name)' already on new PluginType"
        }
    }

    # Custom API auf neuen Type umhaengen
    if ($customApiTypeId) {
        $apisUri = "$BaseUrl/customapis?" + '$filter=uniquename eq ' + "'jbe_RunIntegrationTests'"
        $apis = (Invoke-DataverseApi -Uri $apisUri).value
        if ($apis.Count -gt 0 -and $apis[0]._plugintypeid_value -ne $customApiTypeId) {
            Write-Host "  Custom API jbe_RunIntegrationTests: updating PluginType binding..."
            $patchBody = @{ "plugintypeid@odata.bind" = "/plugintypes($customApiTypeId)" } | ConvertTo-Json
            Invoke-DataverseApi -Method Patch -Uri "$BaseUrl/customapis($($apis[0].customapiid))" `
                -ExtraHeaders @{ 'If-Match' = '*' } -Body $patchBody
            Write-Host "    OK"
        }
    }

    Write-Host ""
    Write-Host "Deploy complete. Neue Assembly in Sandbox aktiv (~10s Cache-Reload)."
    exit 0
}

# ════════════════════════════════════════════════════════════════════
#  5. Mode Install: Custom API + Steps + PreImage anlegen
# ════════════════════════════════════════════════════════════════════

Write-Host ""
Write-Host "=== Mode: Install ==="

# 5.1 SDK Message + Filter IDs holen
$createMsgUri = "$BaseUrl/sdkmessages?" + '$filter=name eq ' + "'Create'" + '&$select=sdkmessageid'
$createMsgId = (Invoke-DataverseApi -Uri $createMsgUri).value[0].sdkmessageid
$updateMsgUri = "$BaseUrl/sdkmessages?" + '$filter=name eq ' + "'Update'" + '&$select=sdkmessageid'
$updateMsgId = (Invoke-DataverseApi -Uri $updateMsgUri).value[0].sdkmessageid

$createFilterUri = "$BaseUrl/sdkmessagefilters?" + '$filter=primaryobjecttypecode eq ' + "'jbe_testrun'" + ' and _sdkmessageid_value eq ' + $createMsgId + '&$select=sdkmessagefilterid'
$createFilterId = (Invoke-DataverseApi -Uri $createFilterUri).value[0].sdkmessagefilterid
$updateFilterUri = "$BaseUrl/sdkmessagefilters?" + '$filter=primaryobjecttypecode eq ' + "'jbe_testrun'" + ' and _sdkmessageid_value eq ' + $updateMsgId + '&$select=sdkmessagefilterid'
$updateFilterId = (Invoke-DataverseApi -Uri $updateFilterUri).value[0].sdkmessagefilterid

Write-Host "SDK Messages: Create=$createMsgId, Update=$updateMsgId"
Write-Host "Filters: Create(jbe_testrun)=$createFilterId, Update(jbe_testrun)=$updateFilterId"

$solHeaders = @{ 'MSCRM.SolutionName' = $SolutionName; 'Prefer' = 'return=representation' }

# 5.2 Step: Create on jbe_testrun
$stepCreateBody = @{
    name                            = 'D365TestCenter.RunTestsOnStatusChange: Create of jbe_testrun'
    mode                            = 1  # Async
    stage                           = 40  # PostOperation
    rank                            = 1
    asyncautodelete                 = $false
    'sdkmessageid@odata.bind'       = "/sdkmessages($createMsgId)"
    'sdkmessagefilterid@odata.bind' = "/sdkmessagefilters($createFilterId)"
    'plugintypeid@odata.bind'       = "/plugintypes($typeId)"
} | ConvertTo-Json
$stepCreate = Invoke-DataverseApi -Method Post -Uri "$BaseUrl/sdkmessageprocessingsteps" -ExtraHeaders $solHeaders -Body $stepCreateBody
$stepCreateId = $stepCreate.sdkmessageprocessingstepid
Write-Host "Step registered (Create): $stepCreateId"

# 5.3 Step: Update on jbe_testrun (mit FilteringAttributes)
$stepUpdateBody = @{
    name                            = 'D365TestCenter.RunTestsOnStatusChange: Update of jbe_testrun'
    mode                            = 1  # Async
    stage                           = 40  # PostOperation
    rank                            = 1
    asyncautodelete                 = $false
    filteringattributes             = 'jbe_teststatus,jbe_batchoffset'
    'sdkmessageid@odata.bind'       = "/sdkmessages($updateMsgId)"
    'sdkmessagefilterid@odata.bind' = "/sdkmessagefilters($updateFilterId)"
    'plugintypeid@odata.bind'       = "/plugintypes($typeId)"
} | ConvertTo-Json
$stepUpdate = Invoke-DataverseApi -Method Post -Uri "$BaseUrl/sdkmessageprocessingsteps" -ExtraHeaders $solHeaders -Body $stepUpdateBody
$stepUpdateId = $stepUpdate.sdkmessageprocessingstepid
Write-Host "Step registered (Update): $stepUpdateId"

# 5.4 PreImage auf Update-Step
$imageBody = @{
    name                                    = 'PreImage'
    entityalias                             = 'PreImage'
    imagetype                               = 0  # PreImage
    messagepropertyname                     = 'Target'
    attributes                              = 'jbe_teststatus'
    'sdkmessageprocessingstepid@odata.bind' = "/sdkmessageprocessingsteps($stepUpdateId)"
} | ConvertTo-Json
Invoke-DataverseApi -Method Post -Uri "$BaseUrl/sdkmessageprocessingstepimages" -ExtraHeaders $solHeaders -Body $imageBody | Out-Null
Write-Host "PreImage registered on Update step"

# 5.5 Custom API Check/Create
if ($customApiTypeId) {
    $apisUri = "$BaseUrl/customapis?" + '$filter=uniquename eq ' + "'jbe_RunIntegrationTests'"
    $apis = (Invoke-DataverseApi -Uri $apisUri).value
    if ($apis.Count -eq 0) {
        Write-Host "Custom API jbe_RunIntegrationTests nicht vorhanden - muss separat angelegt werden (Deploy-ProSolution.ps1)"
    } elseif ($apis[0]._plugintypeid_value -ne $customApiTypeId) {
        Write-Host "Custom API jbe_RunIntegrationTests: updating PluginType binding..."
        $patchBody = @{ "plugintypeid@odata.bind" = "/plugintypes($customApiTypeId)" } | ConvertTo-Json
        Invoke-DataverseApi -Method Patch -Uri "$BaseUrl/customapis($($apis[0].customapiid))" `
            -ExtraHeaders @{ 'If-Match' = '*' } -Body $patchBody
        Write-Host "  OK"
    } else {
        Write-Host "Custom API jbe_RunIntegrationTests bereits korrekt verbunden"
    }
}

Write-Host ""
Write-Host "=== Install complete ==="
Write-Host "  Package ID:      $packageId"
Write-Host "  PluginType ID:   $typeId"
Write-Host "  Step Create:     $stepCreateId"
Write-Host "  Step Update:     $stepUpdateId"
Write-Host ""
Write-Host "Neue Assembly ist in der Sandbox aktiv (~10s Cache-Reload)."
