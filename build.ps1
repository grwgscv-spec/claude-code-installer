$ErrorActionPreference = 'Stop'
# 清空旧发布产物，保证每次全量重新生成（避免残留/部分产物混入）
Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Publishing ClaudeCodeInstaller (win-x64 self-contained single file)..."
dotnet publish src/ClaudeCodeInstaller.App -c Release -o dist
$exe = "dist\ClaudeCodeInstaller.App.exe"
if (Test-Path $exe) {
    Move-Item -Force $exe "dist\ClaudeCodeInstaller.exe"
    # 移除 .pdb 符号文件，避免发布物泄露源码路径/内部符号信息
    Get-ChildItem dist -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
    Write-Host "Done: dist\ClaudeCodeInstaller.exe"
} else {
    Write-Error "Publish output not found at $exe"
}
