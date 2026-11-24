# Compila BenQVolumen.exe con el compilador de C# incluido en Windows (.NET Framework 4.x).
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "No se encuentra csc.exe en $csc" }
Get-Process BenQVolumen -ErrorAction SilentlyContinue | Stop-Process -Force
& $csc /nologo /optimize+ /target:winexe /out:"$PSScriptRoot\BenQVolumen.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "$PSScriptRoot\BenQVolumen.cs"
if ($LASTEXITCODE -eq 0) { "OK: $PSScriptRoot\BenQVolumen.exe" }
