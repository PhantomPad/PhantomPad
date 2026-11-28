# Cómo se compila y publica PhantomPad

Todo lo necesario para compilar está en este repositorio. No hace falta nada que viva solo en un PC.

## Publicar una versión (GitHub Actions)

1. Escribe las novedades en `release-notes/<versión>.md` (por ejemplo `release-notes/1.0.9.md`) y súbelo.
2. Crea y sube la etiqueta de la versión:
   ```
   git tag v1.0.9
   git push phantompad v1.0.9
   ```
3. El workflow `.github/workflows/release.yml` hace el resto en un servidor `windows-latest`:
   - instala .NET 8 y `vpk` (Velopack 1.2.161);
   - descarga los drivers oficiales y verifica su huella y su firma (`build/get-drivers.ps1`);
   - compila en Release x64 (`dotnet publish`);
   - empaqueta con Velopack (`vpk pack`), con el paquete "delta" a partir de la versión anterior;
   - crea la GitHub Release con `PhantomPad-win-Setup.exe`, el portable y los paquetes de actualización.

   La versión sale de la etiqueta (`v1.0.9` → `1.0.9`), no hace falta cambiar el `.csproj`.

**Compilación de prueba sin publicar:** pestaña *Actions* → *Release* → *Run workflow*. Los paquetes quedan como artefacto descargable del workflow.

## Compilar en un PC

Requisitos: .NET 8 SDK y `dotnet tool install -g vpk --version 1.2.161`.

```
.\build\release.ps1 -Version 1.0.9
```

El resultado queda en `artifacts\releases` (no se sube a GitHub). Para publicar a mano, usa `gh release create` con esos archivos.

## Drivers incluidos (ViGEmBus y HidHide)

- `build/get-drivers.ps1` los descarga de las Releases oficiales de Nefarius, con **versión fija**.
- Comprueba su **SHA-256** y que estén **firmados por Nefarius**.
- Los deja en `build/drivers`, que no se sube al repositorio. Sus licencias están en `build/driver-licenses`.
- Para cambiar de versión, actualiza la URL y el SHA-256 en el script.

## Firma de código (SignPath), preparada y apagada

Los pasos ya están en el workflow. Para activarlos, en *Settings → Secrets and variables → Actions*:

| Tipo | Nombre | Valor |
|---|---|---|
| Variable | `SIGNPATH_ENABLED` | `true` |
| Secreto | `SIGNPATH_API_TOKEN` | token de la API de SignPath |
| Variable | `SIGNPATH_ORGANIZATION_ID` | id de la organización |
| Variable | `SIGNPATH_PROJECT_SLUG` | proyecto |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | política de firma |
| Variable | `SIGNPATH_APP_ARTIFACT_CONFIG` | configuración para la carpeta de la app (PhantomPad.exe y .dll) |
| Variable | `SIGNPATH_SETUP_ARTIFACT_CONFIG` | configuración para `PhantomPad-win-Setup.exe` |

**Qué se firma y cuándo:**
1. **`PhantomPad.exe` y sus `.dll`, antes de `vpk pack`.** Así el instalador, el portable y los paquetes `.nupkg` de Velopack llevan dentro los archivos ya firmados.
2. **`Setup.exe`, después de `vpk pack`.**

**Qué no se firma:**
- Los `.nupkg` no se vuelven a firmar después de empaquetar. Velopack guarda su huella en `releases.win.json`, y cambiarlos rompería las actualizaciones automáticas.
- Los instaladores de la carpeta `drivers` conservan su firma original de Nefarius. La configuración de artefacto de SignPath debe excluirlos.
