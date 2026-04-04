#requires -Version 7.0
<#
.SYNOPSIS
    Deployt die Pro-Edition: Lite Solution + C#-Assembly (Custom API).

.DESCRIPTION
    1. Ruft Deploy-Solution.ps1 auf (Lite: Entities, OptionSets, Web Resources)
    2. Baut die C#-Solution (dotnet publish)
    3. Registriert PluginAssembly + PluginType
    4. Registriert Custom API (jbe_RunIntegrationTests)
    5. Registriert Plugin Step

.NOTES
    Requires: $headers variable set before running (auth).
    Requires: .NET SDK for dotnet publish.
#>

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot

# -- Config lesen -----------------------------------------------------------
$config  = Get-Content (Join-Path $scriptDir "deploy-config.json") -Raw | ConvertFrom-Json
$baseUrl = $config.resource.TrimEnd("/") + "/api/data/v9.2"
$solName = $config.solutionUniqueName

# -- Auth check -------------------------------------------------------------
if (-not $headers) {
    Write-Error "No authentication headers. Set `$headers before running."
    return
}

$readH = @{ Authorization = $headers["Authorization"]; Accept = "application/json" }
$writeH = @{}
foreach ($k in $headers.Keys) { $writeH[$k] = $headers[$k] }
if (-not $writeH.ContainsKey("Content-Type")) { $writeH["Content-Type"] = "application/json; charset=utf-8" }
$writeH["OData-MaxVersion"] = "4.0"
$writeH["OData-Version"] = "4.0"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  D365 Test Center PRO - Deployment" -ForegroundColor Cyan
Write-Host "  Target: $($config.resource)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# ── Step 1: Lite Solution deployen ────────────────────────────────
Write-Host ""
Write-Host "-- Step 1: Lite Solution (Entities, Web Resources) --" -ForegroundColor Yellow
. "$scriptDir\Deploy-Solution.ps1"

# ── Step 2: Build Assembly ────────────────────────────────────────
Write-Host ""
Write-Host "-- Step 2: Build C# Assembly --" -ForegroundColor Yellow

$backendDir = Join-Path $scriptDir ".." "backend"
$pluginProj = Join-Path $backendDir "D365TestCenter.CrmPlugin" "D365TestCenter.CrmPlugin.csproj"

Write-Host "  Building $pluginProj..." -ForegroundColor Gray
$buildResult = & dotnet publish $pluginProj -c Release -o "$backendDir\publish" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed: $buildResult"
    return
}
Write-Host "  Build successful." -ForegroundColor Green

$assemblyPath = Join-Path $backendDir "publish" "D365TestCenter.CrmPlugin.dll"
if (-not (Test-Path $assemblyPath)) {
    Write-Error "Assembly not found: $assemblyPath"
    return
}

$assemblyContent = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($assemblyPath))
Write-Host "  Assembly: $assemblyPath ($([Math]::Round($assemblyContent.Length / 1024))KB base64)" -ForegroundColor Gray

# ── Step 3: Register Plugin Assembly ──────────────────────────────
Write-Host ""
Write-Host "-- Step 3: Register Plugin Assembly --" -ForegroundColor Yellow

$asmName = "D365TestCenter.CrmPlugin"
$existingAsm = (Invoke-RestMethod -Uri "$baseUrl/pluginassemblies?`$filter=name eq '$asmName'&`$select=pluginassemblyid" -Headers $readH).value

if ($existingAsm.Count -gt 0) {
    $asmId = $existingAsm[0].pluginassemblyid
    Write-Host "  [UPDATE] Assembly '$asmName' ($asmId)" -ForegroundColor Yellow
    $updateBody = [PSCustomObject]@{ content = $assemblyContent } | ConvertTo-Json
    Invoke-RestMethod -Uri "$baseUrl/pluginassemblies($asmId)" -Headers $writeH -Method Patch `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($updateBody))
} else {
    Write-Host "  [CREATE] Assembly '$asmName'" -ForegroundColor Green
    $createBody = [PSCustomObject]@{
        name = $asmName
        content = $assemblyContent
        isolationmode = 2  # Sandbox
        sourcetype = 0     # Database
        description = "D365 Test Center Pro - Custom API Assembly"
    } | ConvertTo-Json
    $asmResp = Invoke-RestMethod -Uri "$baseUrl/pluginassemblies" -Headers $writeH -Method Post `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($createBody)) `
        -ResponseHeadersVariable respH
    $asmId = $asmResp.pluginassemblyid
    if (-not $asmId) {
        $eid = "$($respH['OData-EntityId'])"
        $m = [regex]::Match($eid, '\(([0-9a-f-]+)\)')
        if ($m.Success) { $asmId = $m.Groups[1].Value }
    }
    Write-Host "  Created: $asmId" -ForegroundColor Green

    # Add to solution
    $solBody = [PSCustomObject]@{
        ComponentId = $asmId
        ComponentType = 91  # PluginAssembly
        SolutionUniqueName = $solName
        AddRequiredComponents = $false
    } | ConvertTo-Json
    try {
        Invoke-RestMethod -Uri "$baseUrl/AddSolutionComponent" -Headers $writeH -Method Post `
            -Body ([System.Text.Encoding]::UTF8.GetBytes($solBody))
        Write-Host "  Added to solution '$solName'" -ForegroundColor Gray
    } catch {
        Write-Host "  [WARN] AddSolutionComponent: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# ── Step 4: Register Plugin Type ──────────────────────────────────
Write-Host ""
Write-Host "-- Step 4: Register Plugin Type --" -ForegroundColor Yellow

$typeName = "D365TestCenter.CrmPlugin.RunIntegrationTestsApi"
$existingType = (Invoke-RestMethod -Uri "$baseUrl/plugintypes?`$filter=typename eq '$typeName'&`$select=plugintypeid" -Headers $readH).value

if ($existingType.Count -gt 0) {
    $typeId = $existingType[0].plugintypeid
    Write-Host "  [SKIP] PluginType '$typeName' exists ($typeId)" -ForegroundColor Yellow
} else {
    Write-Host "  [CREATE] PluginType '$typeName'" -ForegroundColor Green
    $typeBody = [PSCustomObject]@{
        typename = $typeName
        friendlyname = "Run Integration Tests"
        name = $typeName
        description = "Custom API: Executes integration test cases"
        "pluginassemblyid@odata.bind" = "/pluginassemblies($asmId)"
    } | ConvertTo-Json
    $typeResp = Invoke-RestMethod -Uri "$baseUrl/plugintypes" -Headers $writeH -Method Post `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($typeBody)) `
        -ResponseHeadersVariable respH
    $typeId = $typeResp.plugintypeid
    if (-not $typeId) {
        $eid = "$($respH['OData-EntityId'])"
        $m = [regex]::Match($eid, '\(([0-9a-f-]+)\)')
        if ($m.Success) { $typeId = $m.Groups[1].Value }
    }
    Write-Host "  Created: $typeId" -ForegroundColor Green
}

# ── Step 5: Register Custom API ───────────────────────────────────
Write-Host ""
Write-Host "-- Step 5: Register Custom API --" -ForegroundColor Yellow

$apiName = "jbe_RunIntegrationTests"
$existingApi = (Invoke-RestMethod -Uri "$baseUrl/customapis?`$filter=uniquename eq '$apiName'&`$select=customapiid" -Headers $readH).value

if ($existingApi.Count -gt 0) {
    Write-Host "  [SKIP] Custom API '$apiName' exists" -ForegroundColor Yellow
} else {
    Write-Host "  [CREATE] Custom API '$apiName'" -ForegroundColor Green
    $apiBody = [PSCustomObject]@{
        uniquename = $apiName
        name = "Run Integration Tests"
        displayname = "Run Integration Tests"
        description = "Executes integration test cases and writes results"
        bindingtype = 0       # Unbound
        isfunction = $false
        isprivate = $false
        allowedcustomprocessingsteptype = 0  # None (sync)
        "PluginTypeId@odata.bind" = "/plugintypes($typeId)"
    } | ConvertTo-Json
    try {
        Invoke-RestMethod -Uri "$baseUrl/customapis" -Headers $writeH -Method Post `
            -Body ([System.Text.Encoding]::UTF8.GetBytes($apiBody))
        Write-Host "  Custom API created." -ForegroundColor Green
    } catch {
        Write-Host "  [WARN] Custom API: $($_.ErrorDetails.Message ?? $_.Exception.Message)" -ForegroundColor Yellow
    }

    # Register request parameter: TestRunId
    Write-Host "  Registering request parameter: TestRunId" -ForegroundColor Gray
    $paramBody = [PSCustomObject]@{
        uniquename = "TestRunId"
        name = "TestRunId"
        displayname = "Test Run ID"
        description = "EntityReference to jbe_testrun record"
        type = 10  # EntityReference
        isoptional = $false
        "CustomAPIId@odata.bind" = "/customapis($($existingApi.Count -gt 0 ? $existingApi[0].customapiid : 'PLACEHOLDER'))"
    } | ConvertTo-Json
    # Note: This needs the custom API ID, which we need to query after creation
    try {
        $apiId = (Invoke-RestMethod -Uri "$baseUrl/customapis?`$filter=uniquename eq '$apiName'&`$select=customapiid" -Headers $readH).value[0].customapiid
        $paramBody = [PSCustomObject]@{
            uniquename = "TestRunId"
            name = "TestRunId"
            displayname = "Test Run ID"
            description = "EntityReference to jbe_testrun record"
            type = 10  # EntityReference
            isoptional = $false
            logicalentityname = "jbe_testrun"
            "CustomAPIId@odata.bind" = "/customapis($apiId)"
        } | ConvertTo-Json
        Invoke-RestMethod -Uri "$baseUrl/customapirequestparameters" -Headers $writeH -Method Post `
            -Body ([System.Text.Encoding]::UTF8.GetBytes($paramBody))
        Write-Host "  Parameter 'TestRunId' registered." -ForegroundColor Green
    } catch {
        Write-Host "  [WARN] Parameter: $($_.ErrorDetails.Message ?? $_.Exception.Message)" -ForegroundColor Yellow
    }

    # Register response properties
    foreach ($respProp in @(
        @{ uniquename = "Success"; type = 0; description = "True if all tests passed" },      # Boolean
        @{ uniquename = "ResultJson"; type = 10; description = "Full result as JSON" },        # String (10=StringType)
        @{ uniquename = "Summary"; type = 10; description = "Human-readable summary" }         # String
    )) {
        Write-Host "  Registering response property: $($respProp.uniquename)" -ForegroundColor Gray
        $rpBody = [PSCustomObject]@{
            uniquename = $respProp.uniquename
            name = $respProp.uniquename
            displayname = $respProp.uniquename
            description = $respProp.description
            type = $respProp.type
            "CustomAPIId@odata.bind" = "/customapis($apiId)"
        } | ConvertTo-Json
        try {
            Invoke-RestMethod -Uri "$baseUrl/customapiresponseproperties" -Headers $writeH -Method Post `
                -Body ([System.Text.Encoding]::UTF8.GetBytes($rpBody))
        } catch {
            Write-Host "  [WARN] $($respProp.uniquename): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

# ── Step 6: Publish ───────────────────────────────────────────────
Write-Host ""
Write-Host "-- Step 6: PublishAllXml --" -ForegroundColor Yellow
try {
    Invoke-RestMethod -Uri "$baseUrl/PublishAllXml" -Headers $writeH -Method Post -Body "{}"
    Write-Host "  Published." -ForegroundColor Green
} catch {
    Write-Host "  [WARN] PublishAllXml: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  D365 Test Center PRO deployed!" -ForegroundColor Green
Write-Host "  Custom API: $apiName" -ForegroundColor Green
Write-Host "  CLI: dotnet run --project backend/D365TestCenter.Cli -- run --org ..." -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
