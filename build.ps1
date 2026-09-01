$ErrorActionPreference = 'Stop'
Write-Host "Publishing ClaudeCodeInstaller (win-x64 self-contained single file)..."
dotnet publish src/ClaudeCodeInstaller.App -c Release -o dist
$exe = "dist\ClaudeCodeInstaller.App.exe"
if (Test-Path $exe) {
    Move-Item -Force $exe "dist\ClaudeCodeInstaller.exe"
    Write-Host "Done: dist\ClaudeCodeInstaller.exe"
} else {
    Write-Error "Publish output not found at $exe"
}
