<#
.SYNOPSIS
    PhantomPad: agrega o actualiza textos en un archivo .resx a partir de un archivo JSON
    { "Clave": "Texto", ... }. Mantiene el resto del archivo igual.
.EXAMPLE
    .\utils\Set-ResxStrings.ps1 -Resx DS4Windows\Translations\Strings.es.resx -Json traducciones.json
#>
param(
    [Parameter(Mandatory)][string]$Resx,
    [Parameter(Mandatory)][string]$Json,
    # Si se indica, solo agrega claves que no existan (no sobrescribe)
    [switch]$OnlyMissing
)

$doc = New-Object System.Xml.XmlDocument
$doc.PreserveWhitespace = $true
$doc.Load((Resolve-Path $Resx).Path)
$root = $doc.DocumentElement

$entries = Get-Content -Raw -Encoding UTF8 $Json | ConvertFrom-Json
$added = 0; $updated = 0; $skipped = 0

foreach ($prop in $entries.PSObject.Properties) {
    $key = $prop.Name; $text = [string]$prop.Value
    $node = $root.SelectSingleNode("data[@name='$key']")
    if ($node) {
        if ($OnlyMissing) { $skipped++; continue }
        $valueNode = $node.SelectSingleNode('value')
        if ($valueNode.InnerText -ne $text) { $valueNode.InnerText = $text; $updated++ } else { $skipped++ }
    }
    else {
        $data = $doc.CreateElement('data')
        $data.SetAttribute('name', $key)
        $space = $doc.CreateAttribute('xml', 'space', 'http://www.w3.org/XML/1998/namespace')
        $space.Value = 'preserve'
        $data.Attributes.Append($space) | Out-Null
        $data.AppendChild($doc.CreateWhitespace("`r`n    ")) | Out-Null
        $value = $doc.CreateElement('value'); $value.InnerText = $text
        $data.AppendChild($value) | Out-Null
        $data.AppendChild($doc.CreateWhitespace("`r`n  ")) | Out-Null
        $root.AppendChild($doc.CreateWhitespace("  ")) | Out-Null
        $root.AppendChild($data) | Out-Null
        $root.AppendChild($doc.CreateWhitespace("`r`n")) | Out-Null
        $added++
    }
}

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Encoding = New-Object System.Text.UTF8Encoding($true)
$writer = [System.Xml.XmlWriter]::Create((Resolve-Path $Resx).Path, $settings)
$doc.Save($writer); $writer.Close()
"{0}: {1} agregadas, {2} actualizadas, {3} sin cambios" -f (Split-Path $Resx -Leaf), $added, $updated, $skipped
