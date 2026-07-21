Write-Host "=== Community Survival Platform - Local Dev ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Starting .NET services and admin-web..." -ForegroundColor Green
Write-Host "  Gateway:      http://localhost:4000  (+ WebSocket + in-process Simulation)"
Write-Host "  Event Log:    http://localhost:4002"
Write-Host "  Progression:  http://localhost:4003"
Write-Host "  Operator API: http://localhost:4004"
Write-Host "  Admin Web:    http://localhost:5173"
Write-Host ""

$services = @(
    @{ Name = "Gateway";    Project = "src/Game.Gateway" },
    @{ Name = "EventLog";   Project = "src/Game.EventLog" },
    @{ Name = "Progression";Project = "src/Game.Progression" },
    @{ Name = "OperatorApi"; Project = "src/Game.OperatorApi" }
)

$jobs = @()
foreach ($svc in $services) {
    $jobs += Start-Job -Name $svc.Name -ScriptBlock {
        param($project)
        Set-Location $using:PSScriptRoot/..
        dotnet run --project $project
    } -ArgumentList $svc.Project
    Write-Host "  Started $($svc.Name)" -ForegroundColor Green
}

# Start admin-web in foreground
Write-Host "  Starting Admin Web..." -ForegroundColor Green
Push-Location "$PSScriptRoot/../clients/admin-web"
npm run dev
Pop-Location

# Cleanup on exit
$jobs | Stop-Job -PassThru | Remove-Job
