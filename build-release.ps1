param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.5.4',
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'release'),
    [string]$SigningParameters = '',
    [string]$VpkToolPath = ''
)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src\SnipFlow.csproj'
$resolvedOutput = [IO.Path]::GetFullPath($OutputRoot)
$publishDirectory = Join-Path $resolvedOutput ('publish-' + $Version)
$toolDirectory = Join-Path ([IO.Path]::GetTempPath()) 'SnipFlowBuildTools\1.2.161'
$packageDirectory = Join-Path $resolvedOutput 'packages'
New-Item -ItemType Directory -Path $publishDirectory,$toolDirectory,$packageDirectory -Force | Out-Null
$toolPath = if ($VpkToolPath) { [IO.Path]::GetFullPath($VpkToolPath) } else { Join-Path $toolDirectory 'vpk.exe' }
if (-not (Test-Path -LiteralPath $toolPath)) {
    dotnet tool install vpk --version 1.2.161 --tool-path $toolDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Velopack tool installation failed.' }
}
dotnet publish $projectPath -c Release --self-contained true -r win-x64 -p:Version=$Version -o $publishDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
$packArguments = @('pack','--packId','SnipFlow','--packTitle','SnipFlow','--packVersion',$Version,'--packDir',$publishDirectory,'--mainExe','SnipFlow.exe','--runtime','win-x64','--channel','win','--framework','vcredist143-x64','--outputDir',$packageDirectory,'--shortcuts','StartMenuRoot','--icon',(Join-Path $PSScriptRoot 'src\Assets\SnipFlow-v3.ico'))
if ($SigningParameters) { $packArguments += @('--signParams',$SigningParameters) }
& $toolPath @packArguments
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
Get-ChildItem -LiteralPath $packageDirectory -File | Select-Object Name,Length
