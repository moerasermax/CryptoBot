# S27-NGROK T3: Open an ngrok HTTPS tunnel for the local CryptoBot Lab.
#
# Purpose:   Expose the local Kestrel (http://0.0.0.0:5000) to phones / remote offices
#            over an ngrok HTTPS URL. Security is enforced server-side by
#            Program.cs's UseForwardedHeaders + IpWhitelistMiddleware (403 for any
#            client IP not listed in Security:AllowedIPs).
#
# Prereqs:
#   1. Install ngrok: `winget install Ngrok.Ngrok` (or https://ngrok.com/download)
#   2. Register the authtoken once:
#        ngrok config add-authtoken <YOUR_TOKEN>
#      (get it from https://dashboard.ngrok.com/get-started/your-authtoken)
#   3. Add the remote device public IP into Security:AllowedIPs in
#      src/CryptoBot.ConsoleApp/appsettings.json
#   4. Start CryptoBot: dotnet run --project src/CryptoBot.ConsoleApp
#
# Usage:     .\scripts\start-ngrok.ps1
# Stop:      Ctrl+C - only terminates the tunnel; CryptoBot itself keeps running.

$ErrorActionPreference = 'Stop'
$port = 5000

# Check if ngrok is installed
$ngrok = Get-Command ngrok -ErrorAction SilentlyContinue
if ($null -eq $ngrok) {
    Write-Host "X ngrok not found. Please install it first:" -ForegroundColor Red
    Write-Host "  winget install Ngrok.Ngrok" -ForegroundColor Yellow
    exit 1
}

# Check if CryptoBot is listening on port 5000
$listening = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
if ($null -eq $listening) {
    Write-Host "! Service on port $port is not running." -ForegroundColor Yellow
    Write-Host "  Please run: dotnet run --project src/CryptoBot.ConsoleApp"
    $continue = Read-Host "Start ngrok anyway? (y/N)"
    if ($continue -ne 'y' -and $continue -ne 'Y') { exit 1 }
}

Write-Host "-> Starting ngrok tunnel for http://localhost:$port" -ForegroundColor Cyan
Write-Host "   (ngrok Dashboard: http://127.0.0.1:4040)" -ForegroundColor DarkGray
Write-Host ""

ngrok http $port
