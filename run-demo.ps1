$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet run --project src/ActivityTracker.Api --launch-profile ActivityTracker.Api
