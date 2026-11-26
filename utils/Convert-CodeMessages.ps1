<#
.SYNOPSIS
    PhantomPad: convierte mensajes en ingles escritos en el codigo C# (AppLogger.LogToGui, LogToTray,
    LogDebug, MessageBox.Show) en llamadas PPText.T("Clave", "texto {0}", args) traducibles.
    Guarda las claves y textos en ingles en un JSON.
.PARAMETER Apply
    Sin -Apply solo muestra lo que haria.
#>
param(
    [string]$Root = "DS4Windows",
    [string]$OutJson = "utils\i18n\code.en.json",
    [switch]$Apply
)

$methods = 'AppLogger\.LogToGui|AppLogger\.LogToTray|(?<![\w.])LogDebug|rootHub\.LogDebug|MessageBox\.Show'
$skipPrefixes = '^(DEBUG:|HID#)'

function Read-StringLiteral([string]$s, [int]$i) {
    # Devuelve @{ End; Interp; Raw } para un literal que empieza en $i ( " o $" )
    $interp = $false
    if ($s[$i] -eq '$') { $interp = $true; $i++ }
    if ($s[$i] -ne '"') { return $null }
    $j = $i + 1; $depth = 0
    while ($j -lt $s.Length) {
        $c = $s[$j]
        if ($c -eq '\') { $j += 2; continue }
        if ($interp -and $c -eq '{') {
            if ($depth -eq 0 -and $j + 1 -lt $s.Length -and $s[$j + 1] -eq '{') { $j += 2; continue }
            $depth++; $j++; continue
        }
        if ($interp -and $c -eq '}' -and $depth -gt 0) { $depth--; $j++; continue }
        if ($c -eq '"' -and $depth -gt 0) {
            # literal anidado dentro de un hueco {...}
            $k = $j + 1; while ($k -lt $s.Length -and $s[$k] -ne '"') { if ($s[$k] -eq '\') { $k++ }; $k++ }
            $j = $k + 1; continue
        }
        if ($c -eq '"') { return @{ End = $j; Interp = $interp; Raw = $s.Substring($i + 1, $j - $i - 1) } }
        $j++
    }
    return $null
}

function ConvertFrom-Escapes([string]$t) {
    return ($t -replace '\\n', "`n" -replace '\\t', "`t" -replace '\\"', '"' -replace '\\\\', '\')
}

function Split-Interpolated([string]$raw, [System.Collections.Generic.List[string]]$exprs) {
    # Convierte el contenido de $"..." en texto con {n} y llena $exprs
    $sb = New-Object System.Text.StringBuilder; $i = 0
    while ($i -lt $raw.Length) {
        $c = $raw[$i]
        if ($c -eq '{' -and $i + 1 -lt $raw.Length -and $raw[$i + 1] -eq '{') { [void]$sb.Append('{{'); $i += 2; continue }
        if ($c -eq '}' -and $i + 1 -lt $raw.Length -and $raw[$i + 1] -eq '}') { [void]$sb.Append('}}'); $i += 2; continue }
        if ($c -eq '{') {
            $j = $i + 1; $paren = 0; $colon = -1
            while ($j -lt $raw.Length) {
                $d = $raw[$j]
                if ($d -eq '"') { $j++; while ($j -lt $raw.Length -and $raw[$j] -ne '"') { if ($raw[$j] -eq '\') { $j++ }; $j++ }; $j++; continue }
                if ($d -eq '(' -or $d -eq '[') { $paren++ }
                elseif ($d -eq ')' -or $d -eq ']') { $paren-- }
                elseif ($d -eq ':' -and $paren -eq 0 -and $colon -lt 0) { $colon = $j }
                elseif ($d -eq '}' -and $paren -eq 0) { break }
                $j++
            }
            $hole = $raw.Substring($i + 1, $j - $i - 1)
            $fmt = ''
            if ($colon -ge 0) { $fmt = ':' + $raw.Substring($colon + 1, $j - $colon - 1); $hole = $raw.Substring($i + 1, $colon - $i - 1) }
            $exprs.Add($hole.Trim()); [void]$sb.Append('{' + ($exprs.Count - 1) + $fmt + '}')
            $i = $j + 1; continue
        }
        [void]$sb.Append($c); $i++
    }
    return (ConvertFrom-Escapes $sb.ToString())
}

function Read-FirstArgument([string]$s, [int]$open) {
    # Desde el '(' de la llamada, devuelve el fin del primer argumento (indice de ',' o ')')
    $i = $open + 1; $paren = 0
    while ($i -lt $s.Length) {
        $c = $s[$i]
        if ($c -eq '"' -or ($c -eq '$' -and $s[$i + 1] -eq '"')) { $lit = Read-StringLiteral $s $i; if (-not $lit) { return -1 }; $i = $lit.End + 1; continue }
        if ($c -eq '(' -or $c -eq '[') { $paren++ }
        elseif ($c -eq ')' -or $c -eq ']') { if ($paren -eq 0) { return $i }; $paren-- }
        elseif ($c -eq ',' -and $paren -eq 0) { return $i }
        $i++
    }
    return -1
}

function Convert-Argument([string]$arg) {
    # Separa por '+' de nivel superior: literales y expresiones
    $parts = @(); $i = 0; $start = 0; $paren = 0
    while ($i -lt $arg.Length) {
        $c = $arg[$i]
        if ($c -eq '"' -or ($c -eq '$' -and $i + 1 -lt $arg.Length -and $arg[$i + 1] -eq '"')) { $lit = Read-StringLiteral $arg $i; $i = $lit.End + 1; continue }
        if ($c -eq '(') { $paren++ } elseif ($c -eq ')') { $paren-- }
        elseif ($c -eq '+' -and $paren -eq 0) { $parts += $arg.Substring($start, $i - $start).Trim(); $start = $i + 1 }
        $i++
    }
    $parts += $arg.Substring($start).Trim()

    $exprs = New-Object System.Collections.Generic.List[string]
    $text = ''
    $hasLiteral = $false
    foreach ($p in $parts) {
        if ($p -match '^\$?"' ) {
            $lit = Read-StringLiteral $p 0
            if (-not $lit -or $lit.End -ne $p.Length - 1) { return $null }
            $hasLiteral = $true
            if ($lit.Interp) { $text += Split-Interpolated $lit.Raw $exprs }
            else { $text += (ConvertFrom-Escapes $lit.Raw) -replace '\{', '{{' -replace '\}', '}}' }
        } else {
            $exprs.Add($p); $text += '{' + ($exprs.Count - 1) + '}'
        }
    }
    if (-not $hasLiteral) { return $null }
    return @{ Text = $text; Exprs = $exprs }
}

function New-Key([string]$text, [hashtable]$used) {
    $clean = ($text -replace '\{\d+[^}]*\}', ' ' -replace '[^A-Za-z0-9 ]', ' ').Trim()
    $words = ($clean -split '\s+' | Where-Object { $_ } | Select-Object -First 7 | ForEach-Object { $_.Substring(0,1).ToUpper() + $_.Substring(1) }) -join ''
    $key = "PPC_$words"; $n = 2
    while ($used.ContainsKey($key) -and $used[$key] -ne $text) { $key = "PPC_$words$n"; $n++ }
    return $key
}

function ConvertTo-CSharpLiteral([string]$t) {
    return '"' + ($t -replace '\\', '\\' -replace '"', '\"' -replace "`r", '' -replace "`n", '\n' -replace "`t", '\t') + '"'
}

$map = [ordered]@{}; $used = @{}; $byText = @{}
if (Test-Path $OutJson) {
    (Get-Content -Raw -Encoding UTF8 $OutJson | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $map[$_.Name] = $_.Value; $used[$_.Name] = $_.Value; $byText[$_.Value] = $_.Name }
}

$files = Get-ChildItem -Path $Root -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch 'Designer|PPText' }
$total = 0; $skipped = @()
foreach ($f in $files) {
    $s = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    $matches2 = [regex]::Matches($s, "(?<m>$methods)\s*\(")
    if ($matches2.Count -eq 0) { continue }
    $sb = New-Object System.Text.StringBuilder; $last = 0; $changed = $false
    foreach ($m in $matches2) {
        $open = $m.Index + $m.Length - 1
        if ($open -lt $last) { continue }
        # ignorar lineas comentadas
        $lineStart = $s.LastIndexOf("`n", $m.Index) + 1
        if ($s.Substring($lineStart, $m.Index - $lineStart) -match '//') { continue }
        $end = Read-FirstArgument $s $open
        if ($end -lt 0) { continue }
        $arg = $s.Substring($open + 1, $end - $open - 1)
        if ($arg -notmatch '"[^"]*[A-Za-z]{3,}') { continue }
        if ($arg -match '^\s*PPText\.') { continue }
        $conv = Convert-Argument $arg.Trim()
        if (-not $conv) { $skipped += "$($f.Name): $($arg.Trim())"; continue }
        if ($conv.Text -match $skipPrefixes -or $conv.Text -notmatch '[A-Za-z]{3,}') { continue }
        if ($byText.ContainsKey($conv.Text)) { $key = $byText[$conv.Text] } else { $key = New-Key $conv.Text $used; $used[$key] = $conv.Text; $byText[$conv.Text] = $key; $map[$key] = $conv.Text }
        $argsTxt = if ($conv.Exprs.Count) { ', ' + ($conv.Exprs -join ', ') } else { '' }
        $replacement = "PPText.T(`"$key`", $(ConvertTo-CSharpLiteral $conv.Text)$argsTxt)"
        [void]$sb.Append($s.Substring($last, $open + 1 - $last)); [void]$sb.Append($replacement)
        $last = $end; $changed = $true; $total++
        if (-not $Apply) { "{0}: {1}" -f $f.Name, $replacement }
    }
    [void]$sb.Append($s.Substring($last))
    if ($Apply -and $changed) {
        $bytes = [IO.File]::ReadAllBytes($f.FullName); $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF
        [IO.File]::WriteAllText($f.FullName, $sb.ToString(), (New-Object Text.UTF8Encoding($bom)))
    }
}
"Mensajes convertidos: $total   Claves: $($map.Count)"
if ($skipped) { "No convertidos (revisar a mano):"; $skipped }
if ($Apply) { $map | ConvertTo-Json | Out-File $OutJson -Encoding utf8 }
