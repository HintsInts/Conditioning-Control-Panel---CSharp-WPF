$ErrorActionPreference = 'Stop'
$RepoRoot = $PSScriptRoot
$sessionEngine = Join-Path $RepoRoot 'ConditioningControlPanel\Services\Session\SessionEngine.cs'
$devicesXaml = Join-Path $RepoRoot 'ConditioningControlPanel\Views\Controls\AppSettings\DevicesSettingsSection.xaml'
$cardPath = Join-Path $RepoRoot '_chaster-card.xaml.snippet'

if (!(Test-Path $sessionEngine) -or !(Test-Path $devicesXaml)) {
    throw "Extract this package into the ROOT of the CCP repository first. Expected ConditioningControlPanel\Services\Session\SessionEngine.cs."
}

# Keep one clean pre-addon backup of the only two existing files modified.
foreach ($path in @($sessionEngine, $devicesXaml)) {
    $backup = $path + '.pre-chaster-addon.bak'
    if (!(Test-Path $backup)) { Copy-Item $path $backup }
}

$engine = Get-Content -Raw -Encoding UTF8 $sessionEngine

if ($engine -notmatch 'ChasterCcpClient\.Instance\s*\.ReportSessionStarted') {
    $startAnchor = @'
            // Fire started event
            SessionStarted?.Invoke(this, EventArgs.Empty);
'@
    $startReplacement = @'
            // Chaster integration: CCP reports only the verified lifecycle fact.
            // Delivery is durable and asynchronous; Chaster/Railway downtime never blocks CCP startup.
            try
            {
                ConditioningControlPanel.Services.Integrations.Chaster.ChasterCcpClient.Instance
                    .ReportSessionStarted(session?.Name, session?.DurationMinutes ?? 0);
            }
            catch (Exception ex)
            {
                App.Logger?.Warning(ex, "Chaster CCP start report could not be queued");
            }

            // Fire started event
            SessionStarted?.Invoke(this, EventArgs.Empty);
'@
    if (!$engine.Contains($startAnchor)) {
        throw 'SessionEngine start hook could not be inserted. The upstream file changed; restore the backup and stop.'
    }
    $engine = $engine.Replace($startAnchor, $startReplacement)
}

if ($engine -notmatch 'ChasterCcpClient\.Instance\s*\.ReportSessionEnded') {
    $endAnchor = @'
            var finalElapsedTime = ElapsedTime;

            HangContext.Leave("session:" + (_currentSession?.Name ?? "?"));
'@
    $endReplacement = @'
            var finalElapsedTime = ElapsedTime;

            // Chaster integration: StopSession already contains the authoritative outcome.
            // Queue before SessionStopped so a re-entrant handler cannot replace the active CCP session id.
            try
            {
                ConditioningControlPanel.Services.Integrations.Chaster.ChasterCcpClient.Instance
                    .ReportSessionEnded(completed, suppressAbandonTracking);
            }
            catch (Exception ex)
            {
                App.Logger?.Warning(ex, "Chaster CCP end report could not be queued");
            }

            HangContext.Leave("session:" + (_currentSession?.Name ?? "?"));
'@
    if (!$engine.Contains($endAnchor)) {
        throw 'SessionEngine end hook could not be inserted. The upstream file changed; restore the backup and stop.'
    }
    $engine = $engine.Replace($endAnchor, $endReplacement)
}
Set-Content -Path $sessionEngine -Value $engine -Encoding UTF8

$xaml = Get-Content -Raw -Encoding UTF8 $devicesXaml
if ($xaml -notmatch 'x:Name="ChasterCard"') {
    $card = Get-Content -Raw -Encoding UTF8 $cardPath
    $xamlAnchor = @'
        <!-- ============================================================
             PANIC KEY & SHORTCUTS
             ============================================================ -->
'@
    if (!$xaml.Contains($xamlAnchor)) {
        throw 'Devices settings hook could not be inserted. The upstream XAML changed; restore the backup and stop.'
    }
    $xaml = $xaml.Replace($xamlAnchor, $card + "`r`n`r`n" + $xamlAnchor)
    Set-Content -Path $devicesXaml -Value $xaml -Encoding UTF8
}

Write-Host ''
Write-Host 'SUCCESS: Chaster addon is now applied to this CCP checkout.' -ForegroundColor Green
Write-Host 'Next: open GitHub Desktop, review the changes, commit them to feature/chaster-integration, and Push origin.'
