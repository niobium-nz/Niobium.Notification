param(
	[string]$OutputPath = "infra/main.bicepparam",
	[string]$BackupPath = "$OutputPath.backup"
)

$prefix = 'APPSETTING_'
$generatedBlockStart = '// BEGIN GENERATED APP SETTINGS'
$generatedBlockEnd = '// END GENERATED APP SETTINGS'

if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
	throw "Bicep parameters file '$OutputPath' does not exist."
}

if (-not (Test-Path -LiteralPath $BackupPath)) {
	Copy-Item -LiteralPath $OutputPath -Destination $BackupPath
	Write-Host "Backed up Bicep parameters file to '$BackupPath'."
}

$content = Get-Content -Raw -LiteralPath $OutputPath
$generatedBlockPattern = "(?ms)\r?\n?$([regex]::Escape($generatedBlockStart)).*?$([regex]::Escape($generatedBlockEnd))\r?\n?"
$content = [regex]::Replace($content, $generatedBlockPattern, '')

if ($content -match '(?m)^\s*param\s+appSettings\s*=') {
	throw "Bicep parameters file '$OutputPath' already defines 'appSettings' outside the generated block."
}

$environmentVariables = Get-ChildItem Env: |
	Where-Object { $_.Name.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) } |
	Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name.Substring($prefix.Length)) } |
	Sort-Object Name

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add($generatedBlockStart)
$lines.Add('param appSettings = [')

foreach ($environmentVariable in $environmentVariables) {
	$settingName = $environmentVariable.Name.Substring($prefix.Length).Replace('\', '\\').Replace("'", "\'")
	$environmentVariableName = $environmentVariable.Name.Replace('\', '\\').Replace("'", "\'")

	Write-Host "Resolved deployment setting '$settingName' from source '$($environmentVariable.Name)'."
	$lines.Add('{0}{{' -f '  ')
	$lines.Add("    name: '$settingName'")
	$lines.Add("    value: readEnvironmentVariable('$environmentVariableName')")
	$lines.Add('  }')
}

$lines.Add(']')
$lines.Add($generatedBlockEnd)

$trimmedContent = $content.TrimEnd("`r", "`n")
$updatedContent = "$trimmedContent`r`n`r`n$($lines -join "`r`n")`r`n"
[System.IO.File]::WriteAllText((Convert-Path -LiteralPath $OutputPath), $updatedContent, [System.Text.UTF8Encoding]::new($false))

Write-Host "Updated Bicep parameters at '$OutputPath' with $($environmentVariables.Count) generated app setting reference(s)."
