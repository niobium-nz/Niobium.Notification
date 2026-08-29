param(
	[string]$OutputPath = "infra/main.bicepparam",
	[string]$BackupPath = "$OutputPath.backup"
)

if (-not (Test-Path -LiteralPath $BackupPath -PathType Leaf)) {
	throw "Bicep parameters backup '$BackupPath' does not exist."
}

Move-Item -LiteralPath $BackupPath -Destination $OutputPath -Force
Write-Host "Restored Bicep parameters from '$BackupPath' to '$OutputPath'."
