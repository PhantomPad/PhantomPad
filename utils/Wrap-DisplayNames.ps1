<#
.SYNOPSIS
    PhantomPad: envuelve textos de pantalla en DS4Windows.PPText.Name("...") en lineas concretas
    (nombres de botones, opciones de menus, titulos de dialogos). Solo para textos que se muestran,
    nunca para datos que se guardan.
#>
param([switch]$Apply)

$root = "DS4Windows"
# archivo => rangos de lineas donde se envuelven los literales que empiezan con mayuscula
$targets = [ordered]@{
    'DS4Forms\ViewModels\ProfileSettingsViewModel.cs' = @(@(39,51), @(731,731), @(1811,1817), @(2810,2882), @(3300,3650), @(3737,3745))
    'DS4Forms\ViewModels\CurrentOutDeviceViewModel.cs' = @(@(202,202), @(217,217))
    'DS4Forms\ViewModels\BindingWindowViewModel.cs'    = @(@(478,478))
    'DS4Forms\BindingWindow.xaml.cs'                   = @(@(74,74))
    'DS4Forms\RecordBox.xaml.cs'                       = @(@(138,300), @(325,325), @(502,502))
    'DS4Forms\SpecialActionEditor.xaml.cs'             = @(@(399,422), @(509,509))
    'DS4Forms\ProfileEditor.xaml.cs'                   = @(@(1324,1324), @(1547,1547))
    'DS4Forms\MainWindow.xaml.cs'                      = @(@(946,946), @(1526,1526), @(1553,1553))
    'DS4Forms\AutoProfiles.xaml.cs'                    = @(@(366,366))
}
$names = [ordered]@{}
foreach ($rel in $targets.Keys) {
    $path = Join-Path $root $rel
    $lines = [IO.File]::ReadAllLines((Resolve-Path $path).Path, [Text.Encoding]::UTF8)
    foreach ($range in $targets[$rel]) {
        for ($n = $range[0]; $n -le $range[1]; $n++) {
            $line = $lines[$n - 1]
            if ($line -match '^\s*//|case "|PPText\.') { continue }
            $new = [regex]::Replace($line, '(?<![\w.$@])"([A-Z0-9][A-Za-z0-9 +\-()/]*[A-Za-z)])"', {
                param($m) $names[$m.Groups[1].Value] = $true; 'DS4Windows.PPText.Name("' + $m.Groups[1].Value + '")' })
            if ($new -ne $line) { if (-not $Apply) { "{0}:{1}: {2}" -f $rel, $n, $new.Trim() }; $lines[$n - 1] = $new }
        }
    }
    if ($Apply) {
        $bytes = [IO.File]::ReadAllBytes((Resolve-Path $path).Path); $bom = $bytes[0] -eq 0xEF
        [IO.File]::WriteAllLines((Resolve-Path $path).Path, $lines, (New-Object Text.UTF8Encoding($bom)))
    }
}
"Nombres distintos: $($names.Count)"
$names.Keys -join ' | '
