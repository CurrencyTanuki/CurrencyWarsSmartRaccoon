param(
    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $false)]
    [string]$ReplayOutputDirectory,

    [Parameter(Mandatory = $false)]
    [string[]]$OnlyOutputs
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\config\templates\1920x1080\pages'
}
if ([string]::IsNullOrWhiteSpace($ReplayOutputDirectory)) {
    $ReplayOutputDirectory =
        Join-Path $PSScriptRoot '..\tests\CurrencyWarsAssistant.Tests\Fixtures\PageReplay'
}

$screenshots = 'C:\Users\zzz81\Pictures\Screenshots'
$definitions = @(
    @{
        Output = 'normal-hud-guide-button.png'
        Replay = 'normal_hud'
        Source = Join-Path $screenshots 'Screenshot 2026-07-24 021335.png'
        # Keep only the lower, invariant portion. The upper-right notification
        # badge appears conditionally and must not be part of the page anchor.
        Rect = @(1560, 46, 80, 42)
    },
    @{
        Output = 'guide-shell-title.png'
        Replay = 'guide_shell'
        Source = 'C:\Users\zzz81\AppData\Local\Temp\codex-clipboard-bddc788b-80f7-40ae-a011-2d2b0b504cfc.png'
        # Only the fixed first line and icon. The second line changes by tab.
        Rect = @(35, 35, 250, 34)
    },
    @{
        Output = 'guide-currency-wars-title.png'
        Replay = 'guide_currency_wars'
        Source = Join-Path $screenshots 'Screenshot 2026-07-24 021347.png'
        Rect = @(275, 290, 265, 95)
    },
    @{
        Output = 'update-popup-header.png'
        Replay = 'update_popup'
        Source = Join-Path $screenshots 'Screenshot 2026-07-24 021350.png'
        Rect = @(400, 230, 820, 85)
    },
    @{
        Output = 'score-popup-title.png'
        Replay = 'score_popup'
        Source = 'C:\Users\zzz81\AppData\Local\Temp\codex-clipboard-71c64d88-d661-4f72-ba48-c587578b7f19.png'
        Rect = @(65, 305, 210, 90)
    },
    @{
        Output = 'currency-wars-home-title.png'
        Replay = 'currency_wars_home'
        Source = Join-Path $screenshots 'Screenshot 2026-07-24 013506.png'
        Rect = @(35, 70, 300, 135)
    },
    @{
        Replay = 'currency_wars_home_recovery_2048x1152'
        Source = 'C:\Users\zzz81\AppData\Local\Temp\codex-clipboard-45812d7d-957e-4f2a-84bf-6f4c2b5e35da.png'
    },
    @{
        Output = 'mode-selection-standard-header.png'
        Replay = 'mode_selection'
        Source = Join-Path $screenshots 'Screenshot 2026-07-24 013541.png'
        Rect = @(570, 130, 410, 55)
    },
    @{
        Output = 'rank-difficulty-header.png'
        Replay = 'rank_difficulty'
        Source = Join-Path $screenshots 'Screenshot 2026-07-24 013547.png'
        # Excludes rank, rank name, statistics and theme color as far as possible.
        Rect = @(690, 285, 520, 65)
    },
    @{
        Output = 'rank-difficulty-in-progress-actions.png'
        Source = Join-Path $PSScriptRoot `
            '..\tests\CurrencyWarsAssistant.Tests\Fixtures\PageReplay\rank_difficulty_in_progress.jpg'
        # Two buttons are unique to an already-running game. Never click them.
        Rect = @(1260, 925, 620, 75)
    },
    @{
        Replay = 'rank_difficulty_a6'
        Source = 'C:\Users\zzz81\AppData\Local\Temp\codex-clipboard-156473a1-534b-4200-9ea7-ae5c0a40bcac.png'
        Rect = @(690, 285, 520, 65)
    },
    @{
        Replay = 'rank_difficulty_a4'
        Source = 'C:\Users\zzz81\AppData\Local\Temp\codex-clipboard-31463b69-9466-47c1-83e8-c04d29cd4f90.png'
        Rect = @(690, 285, 520, 65)
    },
    @{
        Output = 'enemy-overview-leader-label.png'
        Replay = 'enemy_overview'
        Source = Join-Path $screenshots 'Screenshot 2026-07-24 013558.png'
        Rect = @(1280, 735, 515, 85)
    },
    @{
        Output = 'plane-progress-continue.png'
        Replay = 'plane_progress'
        Source = Join-Path $screenshots 'Screenshot 2026-07-24 013604.png'
        Rect = @(872, 952, 180, 35)
    },
    @{
        Output = 'investment-environment-title.png'
        Replay = 'investment_environment'
        Source = Join-Path $screenshots 'Screenshot 2026-07-24 013612.png'
        Rect = @(895, 75, 170, 50)
    },
    @{
        Output = 'preparation-stage-1-1.png'
        Replay = 'preparation_1_1'
        Source = Join-Path $screenshots 'Screenshot 2026-07-24 013625.png'
        Rect = @(393, 23, 155, 80)
    },
    @{
        Output = 'reward-shop-refresh-disabled-panel.png'
        Replay = 'reward_shop_after_two_purchases'
        NativeReplay = 'reward_shop_after_two_purchases_2048x1152.png'
        Source = 'C:\Users\zzz81\AppData\Local\Temp\codex-clipboard-d5966e67-3a9e-4b78-baa6-ba3e27e13e48.png'
        # The refresh panel switches to a dark disabled state whenever the
        # remaining currency is below its cost. Its geometry is unchanged.
        Rect = @(1530, 395, 185, 200)
    },
    @{
        Output = 'abandon-settlement-prompt.png'
        Replay = 'abandon_settlement_prompt'
        Source = 'C:\Users\zzz81\AppData\Local\Temp\codex-clipboard-ab8111bd-73cc-40c6-a801-950f71ff2f0d.png'
        Rect = @(730, 300, 760, 205)
    },
    @{
        Output = 'challenge-failed-title.png'
        Replay = 'challenge_failed'
        Source = 'C:\Users\zzz81\AppData\Local\Temp\codex-clipboard-40351339-4bdb-4241-95c8-00ea0e3286fb.png'
        Rect = @(805, 180, 330, 120)
    },
    @{
        Output = 'incomplete-lineup-prompt-title.png'
        Replay = 'incomplete_lineup_prompt'
        Source = 'C:\Users\zzz81\AppData\Local\Temp\codex-clipboard-bc452241-e975-4d97-bc7d-b642fcd932e1.png'
        Rect = @(900, 390, 120, 55)
    },
    @{
        Output = 'incomplete-lineup-prompt-message.png'
        Source = 'C:\Users\zzz81\AppData\Local\Temp\codex-clipboard-bc452241-e975-4d97-bc7d-b642fcd932e1.png'
        Rect = @(720, 490, 480, 48)
    }
)

if ($null -ne $OnlyOutputs -and $OnlyOutputs.Count -gt 0) {
    $definitions = @($definitions | Where-Object {
        $OnlyOutputs -contains $_.Output -or
            $OnlyOutputs -contains $_.Replay
    })
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$resolvedReplayOutput = [System.IO.Path]::GetFullPath($ReplayOutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
[System.IO.Directory]::CreateDirectory($resolvedReplayOutput) | Out-Null

foreach ($definition in $definitions) {
    if (-not (Test-Path -LiteralPath $definition.Source -PathType Leaf)) {
        throw "Missing source screenshot: $($definition.Source)"
    }

    $source = [System.Drawing.Bitmap]::new($definition.Source)
    if (-not [string]::IsNullOrWhiteSpace($definition.NativeReplay)) {
        $nativeReplay = [System.Drawing.Bitmap]::new($source)
        $nativeGraphics = [System.Drawing.Graphics]::FromImage($nativeReplay)
        try {
            $nativeGraphics.FillRectangle(
                [System.Drawing.Brushes]::Black,
                [System.Drawing.Rectangle]::new(
                    0,
                    [int][Math]::Floor(1025 * $source.Height / 1080),
                    [int][Math]::Ceiling(235 * $source.Width / 1920),
                    [int][Math]::Ceiling(55 * $source.Height / 1080)))
            $nativeReplay.Save(
                (Join-Path $resolvedReplayOutput $definition.NativeReplay),
                [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $nativeGraphics.Dispose()
            $nativeReplay.Dispose()
        }
    }
    $standard = [System.Drawing.Bitmap]::new(
        1920,
        1080,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($standard)
    try {
        $graphics.InterpolationMode =
            [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode =
            [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage(
            $source,
            [System.Drawing.Rectangle]::new(0, 0, 1920, 1080))

        if (-not [string]::IsNullOrWhiteSpace($definition.Replay)) {
            $graphics.FillRectangle(
                [System.Drawing.Brushes]::Black,
                [System.Drawing.Rectangle]::new(0, 1025, 235, 55))
            if ($definition.Replay -eq 'normal_hud') {
                # The party list can contain the player's custom Trailblazer name.
                $graphics.FillRectangle(
                    [System.Drawing.Brushes]::Black,
                    [System.Drawing.Rectangle]::new(1540, 260, 245, 400))
            }
            $replayPath = Join-Path $resolvedReplayOutput "$($definition.Replay).jpg"
            $standard.Save($replayPath, [System.Drawing.Imaging.ImageFormat]::Jpeg)
        }

        if (-not [string]::IsNullOrWhiteSpace($definition.Output)) {
            $rect = [System.Drawing.Rectangle]::new(
                $definition.Rect[0],
                $definition.Rect[1],
                $definition.Rect[2],
                $definition.Rect[3])
            $crop = $standard.Clone(
                $rect,
                [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $outputPath = Join-Path $resolvedOutput $definition.Output
                $crop.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
            }
            finally {
                $crop.Dispose()
            }
        }
    }
    finally {
        $graphics.Dispose()
        $standard.Dispose()
        $source.Dispose()
    }
}

[pscustomobject]@{
    OutputDirectory = $resolvedOutput
    ReplayOutputDirectory = $resolvedReplayOutput
    TemplateCount = $definitions.Count
}
