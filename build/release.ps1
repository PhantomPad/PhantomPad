<#
.SYNOPSIS
    PhantomPad: compila y empaqueta una version con Velopack. Lo usa el workflow de GitHub
    Actions (.github/workflows/release.yml) y tambien sirve en un PC.

.DESCRIPTION
    Etapas (-Stage):
      publish  Descarga/verifica los drivers y hace "dotnet publish" (Release, x64) en
               artifacts\publish. Aqui se pueden firmar PhantomPad.exe y sus .dll (SignPath).
      pack     Descarga de GitHub la ultima version publicada (para crear el paquete "delta"
               de actualizacion pequena) y ejecuta "vpk pack" en artifacts\releases.
               Aqui se puede firmar Setup.exe (SignPath).
      all      publish + pack (por defecto).

    Requisitos: .NET 8 SDK y la herramienta vpk (dotnet tool install -g vpk --version 1.2.161).
    No publica nada en GitHub: eso lo hace el workflow (o gh release create a mano).

.EXAMPLE
    .\build\release.ps1 -Version 1.0.9
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateSet('all', 'publish', 'pack')][string]$Stage = 'all',
    [string]$RepoUrl = 'https://github.com/PhantomPad/PhantomPad',
    [string]$Token = $env:GITHUB_TOKEN,
    # Sin internet o primera version: no se intenta descargar la version anterior
    [switch]$NoDelta
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'DS4Windows\DS4WinWPF.csproj'
$icon = Join-Path $root 'DS4Windows\DS4W.ico'
$publishDir = Join-Path $root 'artifacts\publish'
$releasesDir = Join-Path $root 'artifacts\releases'

$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version no valida: '$Version' (usa por ejemplo 1.0.9)" }

function Invoke-Checked([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what fallo (codigo $LASTEXITCODE)" }
}

if ($Stage -in 'all', 'publish') {
    Write-Host "== Drivers"
    & (Join-Path $PSScriptRoot 'get-drivers.ps1')

    Write-Host "== dotnet publish $Version"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    Invoke-Checked 'dotnet publish' {
        dotnet publish $project -c Release -r win-x64 --self-contained false -p:Platform=x64 `
            -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version `
            -o $publishDir
    }
    $exe = Join-Path $publishDir 'PhantomPad.exe'
    if (-not (Test-Path $exe)) { throw "No se genero $exe" }
    $drv = @(Get-ChildItem (Join-Path $publishDir 'drivers') -Filter *.exe -ErrorAction SilentlyContinue)
    if ($drv.Count -ne 2) { throw "Faltan los drivers en el paquete (hay $($drv.Count) de 2)" }
    Write-Host "Publicado en $publishDir (version $((Get-Item $exe).VersionInfo.FileVersion))"
}

if ($Stage -in 'all', 'pack') {
    if (-not (Test-Path (Join-Path $publishDir 'PhantomPad.exe'))) { throw "Primero hay que ejecutar la etapa publish" }
    New-Item -ItemType Directory -Force -Path $releasesDir | Out-Null

    if (-not $NoDelta) {
        Write-Host "== Version anterior (para el paquete delta)"
        $dl = @('download', 'github', '--repoUrl', $RepoUrl, '--outputDir', $releasesDir)
        if ($Token) { $dl += @('--token', $Token) }
        & vpk @dl
        if ($LASTEXITCODE -ne 0) { Write-Warning "No se pudo descargar la version anterior: solo habra paquete completo." }
    }

    Write-Host "== vpk pack $Version"
    $pack = @('pack', '--packId', 'PhantomPad', '--packVersion', $Version, '--packDir', $publishDir,
        '--mainExe', 'PhantomPad.exe', '--packTitle', 'PhantomPad', '--packAuthors', 'PhantomPad',
        '--icon', $icon, '--framework', 'net8.0-x64-desktop', '--outputDir', $releasesDir)
    $notes = Join-Path $root "release-notes\$Version.md"
    if (Test-Path $notes) { $pack += @('--releaseNotes', $notes) }
    Invoke-Checked 'vpk pack' { & vpk @pack }

    Write-Host "Paquetes en $releasesDir"
    Get-ChildItem $releasesDir -File | Where-Object { $_.Name -notlike '*-full.nupkg' -or $_.Name -like "*-$Version-full.nupkg" } |
        ForEach-Object { "  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB) }
}
