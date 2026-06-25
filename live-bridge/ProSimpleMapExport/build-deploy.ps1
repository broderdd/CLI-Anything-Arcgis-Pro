<#
.SYNOPSIS
    Build the ProSimpleMapExport ArcGIS Pro bridge add-in and deploy it to the
    ArcGIS Pro AddIns folder. After it finishes: RESTART ArcGIS Pro (add-ins load
    only at startup -- there is no hot-reload).

.NOTES
    The deploy target is resolved from the Windows "Documents" known folder via the
    .NET API ([Environment]::GetFolderPath(MyDocuments)), NOT a hardcoded
    C:\Users\<you>\Documents path. That way it always lands where ArcGIS Pro actually
    scans -- even if OneDrive "Known Folder Move" has redirected Documents. (Hardcoding
    the literal path is what caused the add-in to silently deploy to a folder Pro
    wasn't loading from.)

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build-deploy.ps1
    # or just double-click build-deploy.bat
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$proj = Join-Path $PSScriptRoot 'ProSimpleMapExport.csproj'
if (-not (Test-Path $proj)) { Write-Error "Project not found: $proj"; exit 1 }

Write-Host "[1/3] Building (Release): $proj" -ForegroundColor Cyan
dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "Build FAILED (dotnet exit $LASTEXITCODE)."; exit 1 }

Write-Host "[2/3] Locating built add-in package (.esriAddinX)" -ForegroundColor Cyan
$addin = Get-ChildItem (Join-Path $PSScriptRoot 'bin\Release') -Recurse -Filter 'ProSimpleMapExport.esriAddinX' -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $addin) { Write-Error "No ProSimpleMapExport.esriAddinX found under bin\Release (did PackageAddIn run?)."; exit 1 }

# Resolve the AddIns folder from the known folder (follows OneDrive redirection correctly).
$docs   = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
$target = Join-Path $docs 'ArcGIS\AddIns\ArcGISPro'
New-Item -ItemType Directory -Force -Path $target | Out-Null

Write-Host "[3/3] Deploying to: $target" -ForegroundColor Cyan
Copy-Item -LiteralPath $addin.FullName -Destination (Join-Path $target $addin.Name) -Force

Write-Host ""
Write-Host ("Deployed {0} ({1:N1} KB)  ->  {2}" -f $addin.Name, ($addin.Length/1KB), $target) -ForegroundColor Green
Write-Host ">>> RESTART ArcGIS Pro now to load the new build (add-ins load only at startup). <<<" -ForegroundColor Yellow
