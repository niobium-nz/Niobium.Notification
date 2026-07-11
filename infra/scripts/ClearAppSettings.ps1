param(
	[string]$OutputPath = "infra/main.parameters.json"
)

$parameters = @{
	'$schema' = 'https://azure.com'
	contentVersion = '1.0.0.0'
	parameters = @{
		environmentName = @{ value = '${AZURE_ENV_NAME}' }
		appSettings = @{ value = @() }
	}
}

$parameters | ConvertTo-Json -Depth 10 | Set-Content -Path $OutputPath
Write-Host "Cleared generated deployment parameters at '$OutputPath'."
