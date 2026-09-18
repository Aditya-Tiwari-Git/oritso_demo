$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$dataDirectory = (Resolve-Path -LiteralPath (Join-Path $projectRoot 'backend\ItSupport.Api\data')).Path
if (-not $dataDirectory.StartsWith($projectRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to reset data outside the project.' }
Get-ChildItem -LiteralPath $dataDirectory -File | Where-Object { $_.Name -in @('itsupport.db', 'itsupport.db-shm', 'itsupport.db-wal') } | Remove-Item -Force
Write-Host 'Demo database reset. The CTS seed data will be created on the next API start.'
