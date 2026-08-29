$prefix = 'APPSETTING_KV_'
$keyVaultName = [Environment]::GetEnvironmentVariable('keyVaultName')

if ([string]::IsNullOrWhiteSpace($keyVaultName)) {
	throw "The Bicep output environment variable 'keyVaultName' is not set."
}

$secretVariables = Get-ChildItem Env: |
	Where-Object { $_.Name.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) } |
	Sort-Object Name

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

Write-Host "Set $($secretVariables.Count) Key Vault secret(s) from '$prefix' environment variables."