# Build the Milkdown editor (web/) and copy output to Mica/Assets/Editor/

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$webDir = Join-Path $repoRoot "web"
$targetDir = Join-Path (Join-Path (Join-Path $repoRoot "Mica") "Assets") "Editor"

Push-Location $webDir
try {
    pnpm install --frozen-lockfile
    pnpm build
} finally {
    Pop-Location
}

if (Test-Path $targetDir) {
    Remove-Item -Recurse -Force "$targetDir\*"
}
Copy-Item -Recurse -Force "$webDir\dist\*" $targetDir

Write-Host "Editor built and copied to $targetDir" -ForegroundColor Green
