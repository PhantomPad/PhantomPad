<#
.SYNOPSIS
    PhantomPad: busca textos en ingles escritos directamente en los XAML (sin {lex:Loc}).
    Con -Apply, los reemplaza por {lex:Loc Clave} y guarda las claves en un JSON (ingles).
#>
param(
    [string]$Root = "DS4Windows",
    [string]$OutJson = "utils\i18n\xaml.en.json",
    [switch]$Apply
)

$attrPattern = '(?<attr>\b(?:Content|Text|Header|ToolTip|Title|Watermark))="(?<val>[^"{][^"]*)"'
$files = Get-ChildItem -Path $Root -Recurse -Filter *.xaml | Where-Object { $_.FullName -notmatch '\\(bin|obj|Themes)\\' }

# Textos que no se traducen (simbolos, unidades, nombres propios cortos)
$keyboardKeys = 'Esc','Tab','Caps','Space','Enter','Del','Ins','Home','End','PgUp','PgDown','Prt','Scl','Brk','NumLk','BkSpace',
    'LAlt','RAlt','LCtrl','RCtrl','LShift','RShift','LWin','RWin','WASD','DPad','LSU','LSD','LSL','LSR','RSU','RSD','RSL','RSR','LS','RS',
    'PhantomPad','CRC32','FakerInput','H.NotifyIcon','vJoyInterfaceCS','DualSense','DualShock 4','JoyCon','Switch Pro','Xbox 360','Guide'
function Test-Translatable([string]$v) {
    if ($v -match '^\s*(&#x?[0-9A-Fa-f]+;\s*)+$') { return $false }      # iconos (glifos)
    if ($v -match '^\d') { return $false }                                # 100 Hz (10 ms), 1€...
    if ($keyboardKeys -contains $v.Trim()) { return $false }              # teclas y nombres propios
    if ($v -match '^[LRT][XY] (\(|:)|^[LR][XY] \(') { return $false }     # lecturas tecnicas LX (I):
    if ($v -match 'basado en DS4Windows') { return $false }               # cabecera de About (se hace a mano)
    if ($v -match '[^\x00-\x7F]' -and $v -notmatch '&#') { return $false } # ya esta en otro idioma / simbolos
    if ($v -notmatch '[A-Za-z]{2,}') { return $false }
    if ($v -match '^(ms|px|Hz|ID|OK|X|Y|Z|L\d|R\d|LS|RS|L2|R2|DS4|PS|Lb|Rb|XInput|HidHide|ViGEmBus|FakerInput|UDP|OSC|TP|BT|USB)$') { return $false }
    if ($v -cmatch '^\w+Window$|^[a-z]+[A-Z]\w*$') { return $false }  # nombres internos tipo PluginOutDevWindow / camelCase
    if ($v -match '^&\w+;$') { return $false }                          # entidades como &lt;
    return $true
}

function New-Key([string]$text, [hashtable]$used) {
    $clean = ($text -replace "&#x0a;|&#10;|\\n", ' ' -replace '&amp;', 'And' -replace '[^A-Za-z0-9 ]', '').Trim()
    $words = ($clean -split '\s+' | Where-Object { $_ } | ForEach-Object { $_.Substring(0,1).ToUpper() + $_.Substring(1) }) -join ''
    if ($words.Length -gt 40) { $words = $words.Substring(0, 40) }
    $key = "PPX_$words"; $i = 2
    while ($used.ContainsKey($key) -and $used[$key] -ne $text) { $key = "PPX_$words$i"; $i++ }
    return $key
}

$map = [ordered]@{}; $byText = @{}; $used = @{}
# Reusar las claves ya creadas en pasadas anteriores
if (Test-Path $OutJson) {
    (Get-Content -Raw -Encoding UTF8 $OutJson | ConvertFrom-Json).PSObject.Properties | ForEach-Object {
        $map[$_.Name] = $_.Value; $used[$_.Name] = $_.Value; $byText[$_.Value] = $_.Name
    }
}
$report = @()
foreach ($f in $files) {
    $content = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    $hasLex = $content -match 'xmlns:lex='
    $new = [regex]::Replace($content, $attrPattern, {
        param($m)
        $val = $m.Groups['val'].Value
        if (-not (Test-Translatable $val)) { return $m.Value }
        $script:report += [pscustomobject]@{ File = $f.Name; Text = $val; Lex = $hasLex }
        if (-not $hasLex) { return $m.Value }
        if ($byText.ContainsKey($val)) { $key = $byText[$val] } else { $key = New-Key $val $used; $byText[$val] = $key; $used[$key] = $val; $map[$key] = [System.Net.WebUtility]::HtmlDecode($val) }
        return "$($m.Groups['attr'].Value)=`"{lex:Loc $key}`""
    })
    if ($Apply -and $new -ne $content) {
        $bytes = [IO.File]::ReadAllBytes($f.FullName); $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF
        [IO.File]::WriteAllText($f.FullName, $new, (New-Object Text.UTF8Encoding($bom)))
    }
}

"Textos encontrados: $($report.Count)  (unicos: $(($report | Select-Object -Unique Text).Count))"
"Archivos sin lex (no se tocan): " + (($report | Where-Object { -not $_.Lex } | Select-Object -Unique File | ForEach-Object File) -join ', ')
if ($Apply) {
    $map | ConvertTo-Json | Out-File $OutJson -Encoding utf8
    "Claves nuevas: $($map.Count) -> $OutJson"
} else {
    $report | Group-Object File | Sort-Object Count -Descending | Select-Object Count, Name | Format-Table -AutoSize | Out-String
}
