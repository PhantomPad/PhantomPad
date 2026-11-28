<#
.SYNOPSIS
    PhantomPad: descarga los instaladores oficiales de ViGEmBus y HidHide (Nefarius) que van
    dentro del paquete, con version fija, y comprueba su huella SHA-256 y su firma digital.

.DESCRIPTION
    Los .exe NO se guardan en el repositorio: se descargan de las Releases oficiales de
    Nefarius en GitHub. Si la huella no coincide, el script se para (no se empaqueta nada
    que no sea exactamente el instalador esperado). Los drivers conservan su firma original
    de Nefarius: PhantomPad no los vuelve a firmar.

    Resultado: build\drivers\ con los dos instaladores y sus licencias. El proyecto
    (DS4WinWPF.csproj) los copia a la carpeta "drivers" de la app.

.EXAMPLE
    .\build\get-drivers.ps1
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'drivers')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Para actualizar un driver: cambiar Url y Sha256 (Get-FileHash -Algorithm SHA256) a la vez.
$drivers = @(
    @{
        Name   = 'ViGEmBus_1.22.0_x64_x86_arm64.exe'
        Url    = 'https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe'
        Sha256 = '89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A'
    },
    @{
        Name   = 'HidHide_1.5.230_x64.exe'
        Url    = 'https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe'
        Sha256 = 'F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6'
    }
)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

foreach ($d in $drivers) {
    $target = Join-Path $OutDir $d.Name

    if (Test-Path $target) {
        if ((Get-FileHash $target -Algorithm SHA256).Hash -eq $d.Sha256) {
            Write-Host "OK (ya estaba): $($d.Name)"
            continue
        }
        Remove-Item $target -Force
    }

    Write-Host "Descargando $($d.Name)..."
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("phantompad-driver-" + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        Invoke-WebRequest -Uri $d.Url -OutFile $temp -UseBasicParsing
        $hash = (Get-FileHash $temp -Algorithm SHA256).Hash
        if ($hash -ne $d.Sha256) {
            throw "Huella SHA-256 distinta para $($d.Name): esperada $($d.Sha256), recibida $hash. No se usa."
        }
        $sig = Get-AuthenticodeSignature $temp
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike '*Nefarius Software Solutions*') {
            throw "La firma digital de $($d.Name) no es valida o no es de Nefarius ($($sig.Status))."
        }
        Move-Item $temp $target -Force
        Write-Host "OK (descargado y verificado): $($d.Name)"
    }
    finally {
        if (Test-Path $temp) { Remove-Item $temp -Force }
    }
}

# Licencias (estan en el repositorio): ViGEmBus BSD-3-Clause, HidHide MIT
Copy-Item (Join-Path $PSScriptRoot 'driver-licenses\*.txt') $OutDir -Force
Write-Host "Drivers listos en $OutDir"
