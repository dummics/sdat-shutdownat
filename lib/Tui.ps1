Set-StrictMode -Version Latest

$script:SdatSpectreProviderChecked = $false
$script:SdatSpectreProviderLoaded = $false

function Import-SdatSpectreProvider {
    if ($script:SdatSpectreProviderChecked) { return $script:SdatSpectreProviderLoaded }
    $script:SdatSpectreProviderChecked = $true
    $script:SdatSpectreProviderLoaded = $false

    $modulePath = "C:\Users\domix\.codex\vendor_imports\powershell\PwshSpectreConsole\2.6.3\PwshSpectreConsole.psd1"
    if (-not (Test-Path -LiteralPath $modulePath)) { return $false }

    try {
        Import-Module $modulePath -Force -ErrorAction Stop | Out-Null
        $script:SdatSpectreProviderLoaded = $true
    } catch {
        $script:SdatSpectreProviderLoaded = $false
    }

    return $script:SdatSpectreProviderLoaded
}

function Escape-SdatSpectre {
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return "" }
    if (Get-Command Get-SpectreEscapedText -ErrorAction SilentlyContinue) {
        return (Get-SpectreEscapedText $Text)
    }
    return $Text.Replace("[", "[[").Replace("]", "]]")
}

function Remove-SdatSpectreMarkup {
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return "" }
    return ([regex]::Replace($Text, '\[[^\]]+\]', ''))
}

function Write-SdatSpectrePanel {
    param(
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines,
        [string]$Color = "Grey35"
    )
    if (-not (Import-SdatSpectreProvider)) { return $false }
    try {
        $markup = [Spectre.Console.Markup]::new(($Lines -join "`n"))
        $panel = [Spectre.Console.Panel]::new($markup)
        $panel.Header = [Spectre.Console.PanelHeader]::new($Title)
        $panel.Border = [Spectre.Console.BoxBorder]::Rounded
        $panel.BorderStyle = [Spectre.Console.Style]::new([Spectre.Console.Color]::Grey35)
        $panel | Out-Host
        return $true
    } catch {
        return $false
    }
}

function Write-SdatStatusView {
    param(
        [Parameter(Mandatory)][string[]]$Lines,
        [Parameter(Mandatory)][string[]]$Hints
    )
    $all = @("[bold deepskyblue1]SDAT[/] [grey58]shutdown at[/]", "") + $Lines + @("", "[grey58]Quick input[/]") + $Hints
    if (Write-SdatSpectrePanel -Title "[deepskyblue1]Status[/]" -Lines $all -Color "Grey35") { return }

    Write-Host "SDAT" -ForegroundColor Cyan
    Write-Hr
    foreach ($line in $Lines) { Write-Host (Remove-SdatSpectreMarkup -Text $line) }
    Write-Hr
    foreach ($hint in $Hints) { Write-Host (Remove-SdatSpectreMarkup -Text $hint) -ForegroundColor DarkGray }
}

function Write-SdatHelpView {
    param([Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines)
    $escaped = @($Lines | ForEach-Object { "[grey78]$(Escape-SdatSpectre $_)[/]" })
    if (Write-SdatSpectrePanel -Title "[deepskyblue1]SDAT help[/]" -Lines $escaped -Color "Grey35") { return }
    foreach ($line in $Lines) { Write-Host $line }
}

function Get-ConsoleWidthSafe {
    try {
        $w = [Console]::WindowWidth
        if ($w -gt 0) { return $w }
    } catch { }
    return 100
}

function Write-ConsoleAt {
    param(
        [int]$Left,
        [int]$Top,
        [AllowNull()][string]$Text,
        [ConsoleColor]$ForegroundColor,
        [ConsoleColor]$BackgroundColor,
        [switch]$ClearToEnd
    )

    $oldFg = [Console]::ForegroundColor
    $oldBg = [Console]::BackgroundColor
    try {
        [Console]::SetCursorPosition([Math]::Max(0, $Left), [Math]::Max(0, $Top))
    } catch {
        return
    }

    try {
        if ($PSBoundParameters.ContainsKey('ForegroundColor')) { [Console]::ForegroundColor = $ForegroundColor }
        if ($PSBoundParameters.ContainsKey('BackgroundColor')) { [Console]::BackgroundColor = $BackgroundColor }

        $out = if ($null -eq $Text) { "" } else { [string]$Text }
        if ($ClearToEnd) {
            $w = Get-ConsoleWidthSafe
            if ($out.Length -lt ($w - 1)) {
                $out = $out + (' ' * (($w - 1) - $out.Length))
            } else {
                $out = $out.Substring(0, [Math]::Max(0, $w - 1))
            }
        }
        [Console]::Write($out)
    } catch {
    } finally {
        try {
            [Console]::ForegroundColor = $oldFg
            [Console]::BackgroundColor = $oldBg
        } catch { }
    }
}

function New-TuiNotice {
    param(
        [Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)][string]$Message
    )
    return [pscustomobject]@{ Kind = $Kind; Message = $Message }
}

function Write-Hr {
    $w = Get-ConsoleWidthSafe
    Write-Host ('-' * [Math]::Max(10, $w - 1)) -ForegroundColor DarkGray
}

function Write-NoticeBar {
    param($Notice)
    if ($null -eq $Notice) { return }
    $tag = ""
    $tagColor = 'Red'
    $msgColor = 'White'

    if ($Notice.Kind -eq 'error') { $tag = 'ERROR'; $tagColor = 'Red' }
    elseif ($Notice.Kind -eq 'info') { $tag = 'INFO'; $tagColor = 'Cyan'; $msgColor = 'Gray' }
    else { $tag = $Notice.Kind.ToUpperInvariant(); $tagColor = 'Red' }

    Write-Host ("[$tag] ") -NoNewline -ForegroundColor $tagColor
    Write-Host $Notice.Message -ForegroundColor $msgColor
    Write-Hr
}

function Write-SdatHeaderLine {
    param([AllowNull()][string]$Line)
    $plain = Remove-SdatSpectreMarkup -Text $Line
    if ([string]::IsNullOrWhiteSpace($plain)) {
        Write-Host ""
        return
    }

    if ($plain -match '^(One-time|Daily|Skip|Rules)\s{2,}(.*)$') {
        $label = $Matches[1]
        $value = $Matches[2]
        $valueColor = 'Gray'
        if ($label -eq 'Daily') { $valueColor = 'Cyan' }
        if ($label -eq 'One-time' -and $value -notmatch '^none\b') { $valueColor = 'Yellow' }
        if ($label -eq 'Skip' -and $value -notmatch '^none\b') { $valueColor = 'Yellow' }
        if ($label -eq 'Rules') { $valueColor = 'DarkGray' }

        Write-Host ("{0,-9} " -f $label) -NoNewline -ForegroundColor DarkGray
        Write-Host $value -ForegroundColor $valueColor
        return
    }

    Write-Host $plain -ForegroundColor DarkGray
}

function Write-SdatHeaderLineAt {
    param(
        [Parameter(Mandatory)][int]$Top,
        [AllowNull()][string]$Line,
        [ConsoleColor]$BackgroundColor
    )
    $plain = Remove-SdatSpectreMarkup -Text $Line
    if ([string]::IsNullOrWhiteSpace($plain)) {
        Write-ConsoleAt -Left 0 -Top $Top -Text "" -ForegroundColor DarkGray -BackgroundColor $BackgroundColor -ClearToEnd
        return
    }

    if ($plain -match '^(One-time|Daily|Skip|Rules)\s{2,}(.*)$') {
        $label = $Matches[1]
        $value = $Matches[2]
        $valueColor = [ConsoleColor]::Gray
        if ($label -eq 'Daily') { $valueColor = [ConsoleColor]::Cyan }
        if ($label -eq 'One-time' -and $value -notmatch '^none\b') { $valueColor = [ConsoleColor]::Yellow }
        if ($label -eq 'Skip' -and $value -notmatch '^none\b') { $valueColor = [ConsoleColor]::Yellow }
        if ($label -eq 'Rules') { $valueColor = [ConsoleColor]::DarkGray }

        $prefix = ("{0,-9} " -f $label)
        Write-ConsoleAt -Left 0 -Top $Top -Text ($prefix + $value) -ForegroundColor $valueColor -BackgroundColor $BackgroundColor -ClearToEnd
        Write-ConsoleAt -Left 0 -Top $Top -Text $prefix -ForegroundColor DarkGray -BackgroundColor $BackgroundColor
        return
    }

    Write-ConsoleAt -Left 0 -Top $Top -Text $plain -ForegroundColor DarkGray -BackgroundColor $BackgroundColor -ClearToEnd
}

function Show-SdatMainMenu {
    param(
        [Parameter(Mandatory)][string]$Title,
        [string]$Header = "",
        $Notice,
        [AllowNull()][string[]]$Options
    )
    $options = if ($Options -and $Options.Count -gt 0) {
        $Options
    } else {
        @(
            "One-time (volatile)",
            "Daily (permanent)",
            "Toggle skip next permanent"
        )
    }
    $idx = 0

    $consoleReady = $true
    try { $null = [Console]::WindowWidth } catch { $consoleReady = $false }

    if (-not $consoleReady) {
        while ($true) {
            Clear-Host
            Write-Host $Title -ForegroundColor Cyan
            Write-Hr
            Write-NoticeBar -Notice $Notice
            if ($Header) {
                foreach ($line in ($Header -split "`r?`n")) { Write-SdatHeaderLine -Line $line }
                Write-Hr
            }
            Write-Host ""
            for ($i = 0; $i -lt $options.Count; $i++) {
                $line = ("{0}) {1}" -f ($i + 1), $options[$i])
                if ($i -eq $idx) { Write-Host ("> " + $line) -ForegroundColor Black -BackgroundColor DarkCyan }
                else { Write-Host ("  " + $line) -ForegroundColor Gray }
            }
            Write-Host ""
            Write-Host ("1-{0}, Enter=select  |  Esc=back  |  Ctrl+T=diag" -f $options.Count) -ForegroundColor DarkGray

            $k = [Console]::ReadKey($true)
            if (($k.Key -eq [ConsoleKey]::T) -and (($k.Modifiers -band [ConsoleModifiers]::Control) -ne 0)) { return 99 }
            switch ($k.Key) {
                'UpArrow' { if ($idx -gt 0) { $idx-- } }
                'DownArrow' { if ($idx -lt ($options.Count - 1)) { $idx++ } }
                'D1' { if ($options.Count -gt 0) { return 0 } }
                'D2' { if ($options.Count -gt 1) { return 1 } }
                'D3' { if ($options.Count -gt 2) { return 2 } }
                'D4' { if ($options.Count -gt 3) { return 3 } }
                'D5' { if ($options.Count -gt 4) { return 4 } }
                'NumPad1' { if ($options.Count -gt 0) { return 0 } }
                'NumPad2' { if ($options.Count -gt 1) { return 1 } }
                'NumPad3' { if ($options.Count -gt 2) { return 2 } }
                'NumPad4' { if ($options.Count -gt 3) { return 3 } }
                'NumPad5' { if ($options.Count -gt 4) { return 4 } }
                'Enter' { return $idx }
                'Escape' { return $null }
            }
        }
    }

    $w = Get-ConsoleWidthSafe
    $bg = [Console]::BackgroundColor
    $oldCursor = $true
    try { $oldCursor = [Console]::CursorVisible; [Console]::CursorVisible = $false } catch { }

    $titleFg = [ConsoleColor]::Cyan
    $mutedFg = [ConsoleColor]::DarkGray
    $optFg = [ConsoleColor]::Gray
    $selFg = [ConsoleColor]::Black
    $selBg = [ConsoleColor]::DarkCyan

    function Get-HrLine {
        return ('-' * [Math]::Max(10, $w - 1))
    }

    function Render-OptionLine {
        param(
            [Parameter(Mandatory)][int]$Top,
            [Parameter(Mandatory)][int]$Index,
            [Parameter(Mandatory)][bool]$Selected
        )
        $prefix = if ($Selected) { "> " } else { "  " }
        $text = ("{0}) {1}" -f ($Index + 1), $options[$Index])
        $text = $prefix + $text
        if ($Selected) {
            Write-ConsoleAt -Left 0 -Top $Top -Text $text -ForegroundColor $selFg -BackgroundColor $selBg -ClearToEnd
        } else {
            Write-ConsoleAt -Left 0 -Top $Top -Text $text -ForegroundColor $optFg -BackgroundColor $bg -ClearToEnd
        }
    }

    function Render-Frame {
        param([int]$Current)
        try { [Console]::Clear() } catch { try { Clear-Host } catch { } }
        $row = 0
        $row++
        Write-ConsoleAt -Left 0 -Top $row -Text $Title -ForegroundColor $titleFg -BackgroundColor $bg -ClearToEnd
        $row++
        Write-ConsoleAt -Left 0 -Top $row -Text (Get-HrLine) -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
        $row++

        if ($Notice) {
            $tag = "INFO"
            $tagColor = [ConsoleColor]::Cyan
            $msgColor = [ConsoleColor]::Gray
            if ($Notice.Kind -eq 'error') { $tag = "ERROR"; $tagColor = [ConsoleColor]::Red; $msgColor = [ConsoleColor]::White }
            $line = "[${tag}] $($Notice.Message)"
            Write-ConsoleAt -Left 0 -Top $row -Text $line -ForegroundColor $msgColor -BackgroundColor $bg -ClearToEnd
            Write-ConsoleAt -Left 0 -Top $row -Text "[${tag}]" -ForegroundColor $tagColor -BackgroundColor $bg
            $row++
            Write-ConsoleAt -Left 0 -Top $row -Text (Get-HrLine) -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
            $row++
        }

        if ($Header) {
            foreach ($line in ($Header -split "`r?`n")) {
                Write-SdatHeaderLineAt -Top $row -Line $line -BackgroundColor $bg
                $row++
            }
            Write-ConsoleAt -Left 0 -Top $row -Text (Get-HrLine) -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
            $row++
        }

        $row++
        $optionsTop = $row
        for ($i = 0; $i -lt $options.Count; $i++) {
            Render-OptionLine -Top ($optionsTop + $i) -Index $i -Selected ($i -eq $Current)
        }

        $footerTop = $optionsTop + $options.Count + 1
        Write-ConsoleAt -Left 0 -Top $footerTop -Text "" -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
        Write-ConsoleAt -Left 0 -Top ($footerTop + 1) -Text ("1-{0}, Enter=select  |  Esc=back  |  Ctrl+T=diag" -f $options.Count) -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
        return [pscustomobject]@{ OptionsTop = $optionsTop }
    }

    try {
        $layout = Render-Frame -Current $idx
        while ($true) {
            $k = [Console]::ReadKey($true)
            if (($k.Key -eq [ConsoleKey]::T) -and (($k.Modifiers -band [ConsoleModifiers]::Control) -ne 0)) { return 99 }
            switch ($k.Key) {
                'UpArrow' {
                    if ($idx -gt 0) {
                        $prev = $idx
                        $idx--
                        Render-OptionLine -Top ($layout.OptionsTop + $prev) -Index $prev -Selected $false
                        Render-OptionLine -Top ($layout.OptionsTop + $idx) -Index $idx -Selected $true
                    }
                }
                'DownArrow' {
                    if ($idx -lt ($options.Count - 1)) {
                        $prev = $idx
                        $idx++
                        Render-OptionLine -Top ($layout.OptionsTop + $prev) -Index $prev -Selected $false
                        Render-OptionLine -Top ($layout.OptionsTop + $idx) -Index $idx -Selected $true
                    }
                }
                'D1' { if ($options.Count -gt 0) { return 0 } }
                'D2' { if ($options.Count -gt 1) { return 1 } }
                'D3' { if ($options.Count -gt 2) { return 2 } }
                'D4' { if ($options.Count -gt 3) { return 3 } }
                'D5' { if ($options.Count -gt 4) { return 4 } }
                'NumPad1' { if ($options.Count -gt 0) { return 0 } }
                'NumPad2' { if ($options.Count -gt 1) { return 1 } }
                'NumPad3' { if ($options.Count -gt 2) { return 2 } }
                'NumPad4' { if ($options.Count -gt 3) { return 3 } }
                'NumPad5' { if ($options.Count -gt 4) { return 4 } }
                'Enter' { return $idx }
                'Escape' { return $null }
            }
        }
    } finally {
        try { [Console]::CursorVisible = $oldCursor } catch { }
        try {
            $h = [Console]::WindowHeight
            if ($h -gt 0) {
                [Console]::SetCursorPosition(0, [Math]::Max(0, $h - 1))
                [Console]::WriteLine('')
            }
        } catch { }
    }
}

function Show-SdatDiagnosticsMenu {
    param(
        [Parameter(Mandatory)][string]$Title,
        [string]$Header = "",
        $Notice
    )
    $options = @(
        "Run self-test (dry run)",
        "Show last self-test summary",
        "Tail self-test log",
        "Tail self-test JSONL",
        "Back"
    )
    $idx = 0

    $consoleReady = $true
    try { $null = [Console]::WindowWidth } catch { $consoleReady = $false }

    if (-not $consoleReady) {
        while ($true) {
            Clear-Host
            Write-Host $Title -ForegroundColor Cyan
            Write-Hr
            Write-NoticeBar -Notice $Notice
            if ($Header) { Write-Host $Header -ForegroundColor DarkGray; Write-Hr }
            Write-Host ""
            for ($i = 0; $i -lt $options.Count; $i++) {
                if ($i -eq $idx) { Write-Host ("> " + $options[$i]) -ForegroundColor Black -BackgroundColor DarkCyan }
                else { Write-Host ("  " + $options[$i]) -ForegroundColor Gray }
            }
            Write-Host ""
            Write-Host "Enter=select  |  Esc=back" -ForegroundColor DarkGray

            $k = [Console]::ReadKey($true)
            switch ($k.Key) {
                'UpArrow' { if ($idx -gt 0) { $idx-- } }
                'DownArrow' { if ($idx -lt ($options.Count - 1)) { $idx++ } }
                'Enter' { return $idx }
                'Escape' { return $null }
            }
        }
    }

    $w = Get-ConsoleWidthSafe
    $bg = [Console]::BackgroundColor
    $oldCursor = $true
    try { $oldCursor = [Console]::CursorVisible; [Console]::CursorVisible = $false } catch { }

    $titleFg = [ConsoleColor]::Cyan
    $mutedFg = [ConsoleColor]::DarkGray
    $optFg = [ConsoleColor]::Gray
    $selFg = [ConsoleColor]::Black
    $selBg = [ConsoleColor]::DarkCyan

    function Get-HrLine {
        return ('-' * [Math]::Max(10, $w - 1))
    }

    function Render-OptionLine {
        param(
            [Parameter(Mandatory)][int]$Top,
            [Parameter(Mandatory)][int]$Index,
            [Parameter(Mandatory)][bool]$Selected
        )
        $prefix = if ($Selected) { "> " } else { "  " }
        $text = $prefix + $options[$Index]
        if ($Selected) {
            Write-ConsoleAt -Left 0 -Top $Top -Text $text -ForegroundColor $selFg -BackgroundColor $selBg -ClearToEnd
        } else {
            Write-ConsoleAt -Left 0 -Top $Top -Text $text -ForegroundColor $optFg -BackgroundColor $bg -ClearToEnd
        }
    }

    function Render-Frame {
        param([int]$Current)
        try { [Console]::Clear() } catch { try { Clear-Host } catch { } }
        $row = 0
        $row++
        Write-ConsoleAt -Left 0 -Top $row -Text $Title -ForegroundColor $titleFg -BackgroundColor $bg -ClearToEnd
        $row++
        Write-ConsoleAt -Left 0 -Top $row -Text (Get-HrLine) -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
        $row++

        if ($Notice) {
            $tag = "INFO"
            $tagColor = [ConsoleColor]::Cyan
            $msgColor = [ConsoleColor]::Gray
            if ($Notice.Kind -eq 'error') { $tag = "ERROR"; $tagColor = [ConsoleColor]::Red; $msgColor = [ConsoleColor]::White }
            $line = "[${tag}] $($Notice.Message)"
            Write-ConsoleAt -Left 0 -Top $row -Text $line -ForegroundColor $msgColor -BackgroundColor $bg -ClearToEnd
            Write-ConsoleAt -Left 0 -Top $row -Text "[${tag}]" -ForegroundColor $tagColor -BackgroundColor $bg
            $row++
            Write-ConsoleAt -Left 0 -Top $row -Text (Get-HrLine) -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
            $row++
        }

        if ($Header) {
            foreach ($line in ($Header -split "`r?`n")) {
                Write-ConsoleAt -Left 0 -Top $row -Text $line -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
                $row++
            }
            Write-ConsoleAt -Left 0 -Top $row -Text (Get-HrLine) -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
            $row++
        }

        $row++
        $optionsTop = $row
        for ($i = 0; $i -lt $options.Count; $i++) {
            Render-OptionLine -Top ($optionsTop + $i) -Index $i -Selected ($i -eq $Current)
        }

        $footerTop = $optionsTop + $options.Count + 1
        Write-ConsoleAt -Left 0 -Top $footerTop -Text "" -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
        Write-ConsoleAt -Left 0 -Top ($footerTop + 1) -Text "Enter=select  |  Esc=back" -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
        return [pscustomobject]@{ OptionsTop = $optionsTop }
    }

    try {
        $layout = Render-Frame -Current $idx
        while ($true) {
            $k = [Console]::ReadKey($true)
            switch ($k.Key) {
                'UpArrow' {
                    if ($idx -gt 0) {
                        $prev = $idx
                        $idx--
                        Render-OptionLine -Top ($layout.OptionsTop + $prev) -Index $prev -Selected $false
                        Render-OptionLine -Top ($layout.OptionsTop + $idx) -Index $idx -Selected $true
                    }
                }
                'DownArrow' {
                    if ($idx -lt ($options.Count - 1)) {
                        $prev = $idx
                        $idx++
                        Render-OptionLine -Top ($layout.OptionsTop + $prev) -Index $prev -Selected $false
                        Render-OptionLine -Top ($layout.OptionsTop + $idx) -Index $idx -Selected $true
                    }
                }
                'Enter' { return $idx }
                'Escape' { return $null }
            }
        }
    } finally {
        try { [Console]::CursorVisible = $oldCursor } catch { }
        try {
            $h = [Console]::WindowHeight
            if ($h -gt 0) {
                [Console]::SetCursorPosition(0, [Math]::Max(0, $h - 1))
                [Console]::WriteLine('')
            }
        } catch { }
    }
}

function Read-LineWithEsc {
    param(
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string]$Prompt,
        [string]$Header = ""
    )
    $buf = ""

    $consoleReady = $true
    try { $null = [Console]::WindowWidth } catch { $consoleReady = $false }
    if (-not $consoleReady) {
        while ($true) {
            Clear-Host
            Write-Host $Title -ForegroundColor Cyan
            Write-Hr
            if ($Header) { Write-Host $Header -ForegroundColor DarkGray; Write-Hr }
            Write-Host ""
            Write-Host $Prompt -ForegroundColor Gray
            Write-Host ""
            Write-Host ("{0}> {1}" -f " ", $buf) -ForegroundColor White
            Write-Host ""
            Write-Host "Examples: 2330, 23:30, 2h, 45m, 180s. Enter=confirm | Esc=back | Empty+Enter=cancel" -ForegroundColor DarkGray

            $k = [Console]::ReadKey($true)
            switch ($k.Key) {
                'Escape' { return $null }
                'Enter' { return $buf }
                'Backspace' { if ($buf.Length -gt 0) { $buf = $buf.Substring(0, $buf.Length - 1) } }
                default {
                    $c = $k.KeyChar
                    if ($c -and ($c -match '[0-9a-zA-Z:]') -and $buf.Length -lt 12) {
                        $buf += $c
                    }
                }
            }
        }
    }

    $oldCursor = $true
    try { $oldCursor = [Console]::CursorVisible; [Console]::CursorVisible = $false } catch { }
    $inputTop = 0
    $inputLeft = 0
    $maxLen = 12
    try {
        [Console]::Clear()
        Write-Host $Title -ForegroundColor Cyan
        Write-Hr
        if ($Header) { Write-Host $Header -ForegroundColor DarkGray; Write-Hr }
        Write-Host ""
        Write-Host $Prompt -ForegroundColor Gray
        Write-Host ""

        $inputTop = [Console]::CursorTop
        $inputLeft = 0
        [Console]::Write("> ")
        $inputLeft = [Console]::CursorLeft
        [Console]::WriteLine("")
        Write-Host ""
        Write-Host "Examples: 2330, 23:30, 2h, 45m, 180s. Enter=confirm | Esc=back | Empty+Enter=cancel" -ForegroundColor DarkGray

        $w = Get-ConsoleWidthSafe
        function Render-InputLine {
            param([AllowEmptyString()][string]$Value)
            try {
                [Console]::SetCursorPosition($inputLeft, $inputTop)
                $out = $Value
                if ($out.Length -gt $maxLen) { $out = $out.Substring(0, $maxLen) }
                $pad = [Math]::Max(0, ($w - 3) - $out.Length) # width minus '> ' and 1
                [Console]::Write($out + (' ' * $pad))
                [Console]::SetCursorPosition($inputLeft + [Math]::Min($out.Length, $maxLen), $inputTop)
            } catch { }
        }

        Render-InputLine -Value $buf
        while ($true) {
            $k = [Console]::ReadKey($true)
            switch ($k.Key) {
                'Escape' { return $null }
                'Enter' { return $buf }
                'Backspace' {
                    if ($buf.Length -gt 0) {
                        $buf = $buf.Substring(0, $buf.Length - 1)
                        Render-InputLine -Value $buf
                    }
                }
                default {
                $c = $k.KeyChar
                if ($c -and ($c -match '[0-9a-zA-Z:]') -and $buf.Length -lt $maxLen) {
                    $buf += $c
                    Render-InputLine -Value $buf
                }
                }
            }
        }
    } finally {
        try { [Console]::CursorVisible = $oldCursor } catch { }
        try {
            $h = [Console]::WindowHeight
            if ($h -gt 0) { [Console]::SetCursorPosition(0, [Math]::Max(0, $h - 1)) }
        } catch { }
    }
}
