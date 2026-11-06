$prefix = 'APPSETTING_KV_'
$keyVaultName = [Environment]::GetEnvironmentVariable('keyVaultName')

if ([string]::IsNullOrWhiteSpace($keyVaultName)) {
	throw "The Bicep output environment variable 'keyVaultName' is not set."
}

$secretVariables = Get-ChildItem Env: |
	Where-Object { $_.Name.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) } |
	Sort-Object Name

$functionAppResourceId = $null
if ($secretVariables.Count -gt 0) {
	$functionAppName = [Environment]::GetEnvironmentVariable('functionAppName')
	$resourceGroupName = [Environment]::GetEnvironmentVariable('AZURE_RESOURCE_GROUP')
	if ([string]::IsNullOrWhiteSpace($functionAppName) -or [string]::IsNullOrWhiteSpace($resourceGroupName)) {
		throw "The functionAppName output and AZURE_RESOURCE_GROUP are required to refresh Key Vault references."
	}

	$functionAppResourceId = az resource show --resource-group $resourceGroupName --name $functionAppName --resource-type Microsoft.Web/sites --api-version 2024-11-01 --query id --only-show-errors --output tsv
	if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($functionAppResourceId)) {
		throw "Failed to resolve Function App '$functionAppName' for Key Vault reference refresh."
	}
}

foreach ($secretVariable in $secretVariables) {
	$secretName = $secretVariable.Name.Substring($prefix.Length)
	if ([string]::IsNullOrWhiteSpace($secretName)) {
		throw "Environment variable '$($secretVariable.Name)' does not contain a Key Vault secret name."
	}

	# Replace any underscores in the secret name with dashes for Key Vault
	$kvSecretName = $secretName -replace '_', '-'

	Write-Host "Setting Key Vault secret '$kvSecretName' (from env var '$($secretVariable.Name)') in vault '$keyVaultName'."
	az keyvault secret set --vault-name $keyVaultName --name $kvSecretName --value $secretVariable.Value --only-show-errors --output none
	if ($LASTEXITCODE -ne 0) {
		throw "Failed to set Key Vault secret '$kvSecretName' in vault '$keyVaultName'."
	}
}

if ($secretVariables.Count -gt 0) {
	# App Service may cache versionless references for 24 hours. Refresh only after every
	# secret write succeeds, so deployment does not start with an older signing credential.
	az rest --method post --uri "https://management.azure.com$functionAppResourceId/config/configreferences/appsettings/refresh?api-version=2022-03-01" --only-show-errors --output none
	if ($LASTEXITCODE -ne 0) {
		throw "Failed to refresh Key Vault references for Function App '$functionAppName'."
	}
	Write-Host "Refreshed Function App Key Vault references."
}

Write-Host "Set $($secretVariables.Count) Key Vault secret(s) from '$prefix' environment variables."