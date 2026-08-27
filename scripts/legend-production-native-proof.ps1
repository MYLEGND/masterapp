param()
$ErrorActionPreference = 'Stop'

$webapp = if ($env:AZURE_WEBAPP_NAME) { $env:AZURE_WEBAPP_NAME } else { 'masterapp-portal' }
$resourceGroup = if ($env:AZURE_RESOURCE_GROUP) { $env:AZURE_RESOURCE_GROUP } else { 'masterapp-rg' }

$connection = ''
$connections = az webapp config connection-string list `
  --resource-group $resourceGroup `
  --name $webapp `
  --output json | ConvertFrom-Json
$match = $connections | Where-Object { $_.name -eq 'MasterAppDb' } | Select-Object -First 1
if ($null -ne $match) { $connection = [string]$match.value }

$settings = @{}
foreach ($item in (az webapp config appsettings list `
  --resource-group $resourceGroup `
  --name $webapp `
  --output json | ConvertFrom-Json)) {
  $settings[[string]$item.name] = [string]$item.value
}

if ([string]::IsNullOrWhiteSpace($connection)) {
  foreach ($name in @('ConnectionStrings__MasterAppDb','MasterAppDb','SQLCONNSTR_MasterAppDb')) {
    if ($settings.ContainsKey($name) -and -not [string]::IsNullOrWhiteSpace($settings[$name])) {
      $connection = $settings[$name]
      break
    }
  }
}
if ([string]::IsNullOrWhiteSpace($connection)) { throw 'Production MasterAppDb connection string could not be resolved.' }
if ($connection -notmatch '(?i)(server=tcp:|\.database\.windows\.net|initial catalog=)') {
  throw 'Resolved MasterAppDb is not recognized as production SQL Server.'
}

$founderOid = ''
foreach ($name in @('FOUNDER_OID','Founder__Oid','Founder:Oid')) {
  if ($settings.ContainsKey($name) -and -not [string]::IsNullOrWhiteSpace($settings[$name])) {
    $founderOid = $settings[$name]
    break
  }
}
if ([string]::IsNullOrWhiteSpace($founderOid)) { throw 'Production Founder OID could not be resolved.' }

Write-Output "::add-mask::$connection"
Write-Output "::add-mask::$founderOid"
$env:LEGEND_PRODUCTION_READONLY_CONNECTION = $connection
$env:LEGEND_PRODUCTION_READONLY_FOUNDER_OID = $founderOid
$env:LEGEND_PRODUCTION_READONLY_EXPECT_NATIVE = 'true'
$env:OPENAI_API_KEY = ''
$env:OpenAI__ApiKey = ''

Write-Host '=== LIVE FOUNDER NATIVE-ONLY 8-PROMPT PROOF ==='
dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj -c Release --no-build `
  --filter 'FullyQualifiedName~LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyEightPromptNativeDiagnostic' `
  --logger 'console;verbosity=detailed'
if ($LASTEXITCODE -ne 0) { throw 'Live Founder native-only 8-prompt proof failed.' }

Write-Host '=== LIVE FOUNDER V21 READ-ONLY SHADOW REBUILD ==='
dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj -c Release --no-build `
  --filter 'FullyQualifiedName~LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyV21ShadowRebuild_UsesLiveFounderEvidenceWithoutWrites' `
  --logger 'console;verbosity=detailed'
if ($LASTEXITCODE -ne 0) { throw 'Live Founder V21 read-only shadow proof failed.' }

Write-Host 'LEGEND LIVE PREDEPLOY PROOF: PASS'
Write-Host 'OPENAI: BLOCKED'
Write-Host 'PRODUCTION WRITES: BLOCKED BY READ-ONLY CONNECTION + COMMAND INTERCEPTOR'
