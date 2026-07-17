param(
	[string]$OutputPath = "infra/main.parameters.json"
)

$prefix = 'APPSETTING_'

# Read existing parameters file if present so we preserve any values not overridden by environment variables
$existing = $null
if (Test-Path -Path $OutputPath) {
	$raw = Get-Content -Raw -Path $OutputPath
	if (-not [string]::IsNullOrWhiteSpace($raw)) {
		try {
			$existing = $raw | ConvertFrom-Json -ErrorAction Stop
		} catch {
			# If file exists but isn't valid JSON, treat as no existing content
			$existing = $null
		}
	}
}

# Build a map of existing app settings (if any)
$appSettingsMap = @{}
if ($existing -and $existing.parameters -and $existing.parameters.appSettings -and $existing.parameters.appSettings.value) {
	foreach ($item in $existing.parameters.appSettings.value) {
		if ($item.name) {
			$appSettingsMap[$item.name] = $item.value
		}
	}
}

# Enumerate environment variables with the configured prefix and add/override entries in the map
Get-ChildItem Env: |
	Where-Object { $_.Name.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) } |
	Sort-Object Name |
	ForEach-Object {
		$settingName = $_.Name.Substring($prefix.Length)
		if ([string]::IsNullOrWhiteSpace($settingName)) { return }
		if ([string]::IsNullOrWhiteSpace($_.Value)) { return }

		Write-Host "Resolved deployment setting '$settingName' from source '$($_.Name)'."
		$appSettingsMap[$settingName] = $_.Value
	}

# Convert map back to ordered array of objects expected by ARM template parameters
$appSettings = $appSettingsMap.GetEnumerator() | Sort-Object Name | ForEach-Object {
	@{ name = $_.Key; value = $_.Value }
}

# Build resulting parameters object, preserving other existing parameter entries where possible
$parameters = @{
	'$schema' = if ($existing -and $existing.'$schema') { $existing.'$schema' } else { 'https://azure.com' }
	contentVersion = if ($existing -and $existing.contentVersion) { $existing.contentVersion } else { '1.0.0.0' }
	parameters = @{}
}

if ($existing -and $existing.parameters) {
	foreach ($prop in $existing.parameters.PSObject.Properties) {
		$parameters.parameters[$prop.Name] = $prop.Value
	}
}

# Ensure appSettings is replaced with the merged set
$parameters.parameters.appSettings = @{ value = $appSettings }

# Ensure output directory exists
$directory = Split-Path -Parent $OutputPath
if ($directory -and -not (Test-Path $directory)) {
	New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

# Write merged parameters back to file
$parameters | ConvertTo-Json -Depth 10 | Set-Content -Path $OutputPath
