Set-StrictMode -Version Latest

$script:SdatSpectreProviderChecked = $false
$script:SdatSpectreProviderLoaded = $false
$script:SdatSpectreFrameLines = 0

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
    if ([string]::IsNullOrEmpty($Text)) { return " " }
    if (Get-Command Get-SpectreEscapedText -ErrorAction SilentlyContinue) {
        return (Get-SpectreEscapedText $Text)
    }
    return $Text.Replace("[", "[[").Replace("]", "]]")
}

function Remove-SdatSpectreMarkup {
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return "" }
    return ([regex]::Replace($Text, '\[/?[^\]]+\]', ''))
}

function Write-SdatSpectrePanel {
    param(
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines,
        [string]$Color = "Grey35"
    )
    if (-not (Import-SdatSpectreProvider)) { return $false }
    try {
        $safeLines = @($Lines | ForEach-Object {
            if ([string]::IsNullOrEmpty($_)) { " " } else { [string]$_ }
        })
        $markup = [Spectre.Console.Markup]::new(($safeLines -join "`n"))
        $panel = [Spectre.Console.Panel]::new($markup)
        $panel.Header = [Spectre.Console.PanelHeader]::new($Title)
        $panel.Border = [Spectre.Console.BoxBorder]::Rounded
        $panel.BorderStyle = [Spectre.Console.Style]::new([Spectre.Console.Color]::Grey35)
        $panel.Expand = $false
        $panel.Padding = [Spectre.Console.Padding]::new(1, 0, 1, 0)
        $contentWidth = 0
        foreach ($line in $safeLines) {
            $plain = Remove-SdatSpectreMarkup -Text $line
            if ($plain.Length -gt $contentWidth) { $contentWidth = $plain.Length }
        }
        $panel.Width = [Math]::Min([Math]::Max(44, $contentWidth + 4), [Math]::Max(44, (Get-ConsoleWidthSafe) - 4))
        [Spectre.Console.Align]::Center($panel, $null) | Out-Host
        return $true
    } catch {
        return $false
    }
}

function Write-SdatSpectreFrame {
    param(
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines,
        [int]$EstimatedLineCount = 18
    )
    if (-not (Import-SdatSpectreProvider)) { return $false }
    try {
        if ([Console]::WindowWidth -le 0) { return $false }
        [Console]::SetCursorPosition(0, 0)
        $null = Write-SdatSpectrePanel -Title $Title -Lines $Lines
        $width = [Math]::Max(1, [Console]::WindowWidth - 1)
        $currentTop = [Console]::CursorTop
        $target = [Math]::Max($EstimatedLineCount, $script:SdatSpectreFrameLines)
        for ($line = $currentTop; $line -lt $target; $line++) {
            if ($line -ge [Console]::WindowHeight) { break }
            [Console]::SetCursorPosition(0, $line)
            [Console]::Write(("").PadRight($width))
        }
        [Console]::SetCursorPosition(0, 0)
        $script:SdatSpectreFrameLines = [Math]::Max($EstimatedLineCount, $currentTop)
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

function Write-SdatCommandResult {
    param([Parameter(Mandatory)][string]$Message)

    $safeMessage = Escape-SdatSpectre -Text $Message
    if (Write-SdatSpectrePanel -Title "[deepskyblue1]SDAT[/]" -Lines @("[grey78]$safeMessage[/]") -Color "Grey35") { return }

    Write-Host "SDAT" -ForegroundColor Cyan
    Write-Hr
    Write-Host $Message -ForegroundColor Gray
    Write-Hr
}

function Get-ConsoleWidthSafe {
    try {
        $w = [Console]::WindowWidth
        if ($w -gt 0) { return $w }
    } catch { }
    return 100
}

function Get-SdatCenteredLeft {
    param([int]$Width)
    $w = Get-ConsoleWidthSafe
    return [Math]::Max(0, [int][Math]::Floor(($w - $Width) / 2))
}

function Format-SdatCenteredLine {
    param(
        [AllowNull()][string]$Text,
        [int]$Width = 54
    )
    $value = if ($null -eq $Text) { "" } else { [string]$Text }
    $left = Get-SdatCenteredLeft -Width $Width
    return ((' ' * $left) + $value)
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

function Write-SdatTuiTitle {
    param([string]$Title = "SDAT")
    Write-Host $Title -ForegroundColor Cyan
    Write-Host "shutdown at" -ForegroundColor DarkGray
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
}

function Write-SdatHeaderLine {
    param([AllowNull()][string]$Line)
    $plain = Remove-SdatSpectreMarkup -Text $Line
    if ([string]::IsNullOrWhiteSpace($plain)) {
        Write-Host ""
        return
    }

    if ($plain -match '^(One-time|Daily|Skip|Rules|Power|Daily pause)\s{2,}(.*)$') {
        $label = $Matches[1]
        $value = $Matches[2]
        $valueColor = 'Gray'
        if ($label -eq 'Daily') { $valueColor = 'Cyan' }
        if ($label -eq 'One-time' -and $value -notmatch '^none\b') { $valueColor = 'Yellow' }
        if ($label -eq 'Skip' -and $value -notmatch '^none\b') { $valueColor = 'Yellow' }
        if ($label -eq 'Power') { $valueColor = 'White' }
        if ($label -eq 'Daily pause') { $valueColor = 'Yellow' }
        if ($label -eq 'Rules') { $valueColor = 'DarkGray' }

        Write-Host ("{0,-11} " -f $label) -NoNewline -ForegroundColor DarkGray
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

    if ($plain -match '^(One-time|Daily|Skip|Rules|Power|Daily pause)\s{2,}(.*)$') {
        $label = $Matches[1]
        $value = $Matches[2]
        $valueColor = [ConsoleColor]::Gray
        if ($label -eq 'Daily') { $valueColor = [ConsoleColor]::Cyan }
        if ($label -eq 'One-time' -and $value -notmatch '^none\b') { $valueColor = [ConsoleColor]::Yellow }
        if ($label -eq 'Skip' -and $value -notmatch '^none\b') { $valueColor = [ConsoleColor]::Yellow }
        if ($label -eq 'Power') { $valueColor = [ConsoleColor]::White }
        if ($label -eq 'Daily pause') { $valueColor = [ConsoleColor]::Yellow }
        if ($label -eq 'Rules') { $valueColor = [ConsoleColor]::DarkGray }

        $prefix = ("{0,-11} " -f $label)
        Write-ConsoleAt -Left 0 -Top $Top -Text ($prefix + $value) -ForegroundColor $valueColor -BackgroundColor $BackgroundColor -ClearToEnd
        Write-ConsoleAt -Left 0 -Top $Top -Text $prefix -ForegroundColor DarkGray -BackgroundColor $BackgroundColor
        return
    }

    Write-ConsoleAt -Left 0 -Top $Top -Text $plain -ForegroundColor DarkGray -BackgroundColor $BackgroundColor -ClearToEnd
}

function Get-SdatMenuActionRows {
    param(
        [AllowNull()][string[]]$Options,
        [int]$Selected
    )
    function Find-OptionIndex {
        param([Parameter(Mandatory)][string]$Value)
        for ($i = 0; $i -lt $Options.Count; $i++) {
            if ([string]$Options[$i] -eq $Value) { return $i }
        }
        return -1
    }

    function Format-Cell {
        param(
            [Parameter(Mandatory)][string]$Label,
            [int]$Index
        )
        $plain = (" {0,-6} " -f $Label)
        if ($Index -eq $Selected) { return "[black on deepskyblue1]$plain[/]" }
        return "[grey70]$plain[/]"
    }

    $rows = @(
        [pscustomobject]@{ Name = "Shutdown"; Once = (Find-OptionIndex "Shutdown once"); Daily = (Find-OptionIndex "Shutdown daily") },
        [pscustomobject]@{ Name = "Suspend"; Once = (Find-OptionIndex "Suspend once"); Daily = (Find-OptionIndex "Suspend daily") },
        [pscustomobject]@{ Name = "Restart"; Once = (Find-OptionIndex "Restart once"); Daily = (Find-OptionIndex "Restart daily") }
    )
    $tasksIndex = Find-OptionIndex "Tasks"

    $lines = @()
    foreach ($row in $rows) {
        $name = if ($row.Name -eq "Shutdown") { "[white]  Shutdown  [/]" } else { "[grey70]  $($row.Name.PadRight(8))  [/]" }
        $lines += ("{0}{1}  {2}" -f $name, (Format-Cell -Label "once" -Index $row.Once), (Format-Cell -Label "daily" -Index $row.Daily))
    }
    if ($tasksIndex -ge 0) {
        $taskLine = " Tasks      view scheduled actions "
        if ($tasksIndex -eq $Selected) {
            $lines += " "
            $lines += "[black on deepskyblue1]$taskLine[/]"
        } else {
            $lines += " "
            $lines += "[grey70]$taskLine[/]"
        }
    }
    return $lines
}

function Move-SdatMenuSelection {
    param(
        [Parameter(Mandatory)][int]$Index,
        [Parameter(Mandatory)][string]$Direction,
        [Parameter(Mandatory)][int]$Count
    )
    if ($Count -le 0) { return 0 }
    if ($Count -lt 7) {
        if ($Direction -eq "up") { return [Math]::Max(0, $Index - 1) }
        if ($Direction -eq "down") { return [Math]::Min($Count - 1, $Index + 1) }
        return $Index
    }

    if ($Direction -eq "left") {
        if ($Index -ge 0 -and $Index -le 5 -and ($Index % 2) -eq 1) { return ($Index - 1) }
        return $Index
    }
    if ($Direction -eq "right") {
        if ($Index -ge 0 -and $Index -le 5 -and ($Index % 2) -eq 0) { return ($Index + 1) }
        return $Index
    }
    if ($Direction -eq "up") {
        if ($Index -eq 6) { return 4 }
        if ($Index -ge 2 -and $Index -le 5) { return ($Index - 2) }
        return $Index
    }
    if ($Direction -eq "down") {
        if ($Index -ge 0 -and $Index -le 3) { return ($Index + 2) }
        if ($Index -ge 4 -and $Index -le 5) { return 6 }
        return $Index
    }
    return $Index
}

function Get-SdatMenuKeyAction {
    param([Parameter(Mandatory)]$KeyInfo)

    switch ($KeyInfo.Key) {
        'UpArrow' { return 'up' }
        'DownArrow' { return 'down' }
        'LeftArrow' { return 'left' }
        'RightArrow' { return 'right' }
        'Home' { return 'first' }
        'End' { return 'last' }
        'Enter' { return 'select' }
        'Escape' { return 'back' }
        'W' { return 'up' }
        'S' { return 'down' }
        'A' { return 'left' }
        'D' { return 'right' }
        default { return $null }
    }
}

function Get-SdatMenuTargetIndex {
    param(
        [Parameter(Mandatory)][int]$Index,
        [Parameter(Mandatory)][string]$Action,
        [Parameter(Mandatory)][int]$Count,
        [switch]$Grid
    )

    if ($Count -le 0) { return 0 }
    if ($Action -eq 'first') { return 0 }
    if ($Action -eq 'last') { return ($Count - 1) }
    if ($Grid) { return (Move-SdatMenuSelection -Index $Index -Direction $Action -Count $Count) }
    if ($Action -eq 'up' -or $Action -eq 'left') { return [Math]::Max(0, $Index - 1) }
    if ($Action -eq 'down' -or $Action -eq 'right') { return [Math]::Min($Count - 1, $Index + 1) }
    return $Index
}

function Get-SdatMenuHint {
    param([switch]$Diagnostics)
    if ($Diagnostics) { return 'Arrows / W S move  Enter open  Esc back' }
    return 'Arrows / W A S D move  Enter select  Esc exit  Ctrl+T diagnostics'
}

function Test-SdatTuiInputCharacter {
    param([Parameter(Mandatory)][char]$Character)
    return ([char]::IsLetterOrDigit($Character) -or $Character -in @(':', '.', ',', ' '))
}

function Show-SdatSpectreMainMenu {
    param(
        [Parameter(Mandatory)][string]$Title,
        [string]$Header = "",
        $Notice,
        [AllowNull()][string[]]$Options
    )
    if (-not (Import-SdatSpectreProvider)) { return "fallback" }
    try { if ([Console]::WindowWidth -le 0) { return "fallback" } } catch { return "fallback" }

    $idx = 0
    $oldCursor = $true
    try { $oldCursor = [Console]::CursorVisible; [Console]::CursorVisible = $false } catch { }

    function Render-SpectreMenu {
        param([int]$Current)
        $lines = @("[grey58]shutdown at[/]", " ")
        if ($Header) { $lines += ($Header -split "`r?`n") }
        if ($Notice) {
            $kind = if ($Notice.Kind -eq "error") { "red" } else { "deepskyblue1" }
            $lines += " "
            $lines += "[$kind]$($Notice.Message)[/]"
        }
        $lines += " "
        $lines += (Get-SdatMenuActionRows -Options $Options -Selected $Current)
        $lines += " "
        $lines += "[grey42]$(Get-SdatMenuHint)[/]"
        return (Write-SdatSpectreFrame -Title "[deepskyblue1]SDAT[/]" -Lines $lines -EstimatedLineCount 12)
    }

    try {
        try { [Console]::Clear() } catch { }
        if (-not (Render-SpectreMenu -Current $idx)) { return "fallback" }
        while ($true) {
            $k = [Console]::ReadKey($true)
            if (($k.Key -eq [ConsoleKey]::T) -and (($k.Modifiers -band [ConsoleModifiers]::Control) -ne 0)) { return 99 }
            $action = Get-SdatMenuKeyAction -KeyInfo $k
            if ($action -eq 'select') { return $idx }
            if ($action -eq 'back') { return $null }
            if ($action) {
                $next = Get-SdatMenuTargetIndex -Index $idx -Action $action -Count $Options.Count -Grid
                if ($next -ne $idx) { $idx = $next; $null = Render-SpectreMenu -Current $idx }
            }
        }
    } finally {
        try { [Console]::CursorVisible = $oldCursor } catch { }
        try {
            $target = [Math]::Min([Math]::Max(0, [Console]::WindowHeight - 1), [Math]::Max(0, $script:SdatSpectreFrameLines + 1))
            [Console]::SetCursorPosition(0, $target)
            [Console]::WriteLine("")
        } catch { }
    }
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
            "Shutdown once",
            "Shutdown daily",
            "Suspend once",
            "Suspend daily",
            "Restart once",
            "Restart daily",
            "Tasks"
        )
    }
    $idx = 0

    $consoleReady = $true
    try { $null = [Console]::WindowWidth } catch { $consoleReady = $false }

    if ($consoleReady -and (Import-SdatSpectreProvider)) {
        $spectreResult = Show-SdatSpectreMainMenu -Title $Title -Header $Header -Notice $Notice -Options $options
        if ($spectreResult -ne "fallback") { return $spectreResult }
    }

    if (-not $consoleReady) {
        while ($true) {
            Clear-Host
            Write-SdatTuiTitle -Title $Title
            Write-Host ""
            Write-NoticeBar -Notice $Notice
            if ($Header) {
                foreach ($line in ($Header -split "`r?`n")) { Write-SdatHeaderLine -Line $line }
                Write-Host ""
            }
            Write-Host ""
            for ($i = 0; $i -lt $options.Count; $i++) {
                $line = Format-SdatCenteredLine -Text $options[$i] -Width 34
                if ($i -eq $idx) { Write-Host $line -ForegroundColor Black -BackgroundColor DarkCyan }
                else { Write-Host $line -ForegroundColor Gray }
            }
            Write-Host ""
            Write-Host (Get-SdatMenuHint) -ForegroundColor DarkGray

            $k = [Console]::ReadKey($true)
            if (($k.Key -eq [ConsoleKey]::T) -and (($k.Modifiers -band [ConsoleModifiers]::Control) -ne 0)) { return 99 }
            $action = Get-SdatMenuKeyAction -KeyInfo $k
            if ($action -eq 'select') { return $idx }
            if ($action -eq 'back') { return $null }
            if ($action) { $idx = Get-SdatMenuTargetIndex -Index $idx -Action $action -Count $options.Count -Grid }
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
        $text = Format-SdatCenteredLine -Text $options[$Index] -Width 34
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
        Write-ConsoleAt -Left 0 -Top $footerTop -Text (Get-SdatMenuHint) -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
        Write-ConsoleAt -Left 0 -Top ($footerTop + 1) -Text "" -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
        return [pscustomobject]@{ OptionsTop = $optionsTop }
    }

    try {
        $layout = Render-Frame -Current $idx
        while ($true) {
            $k = [Console]::ReadKey($true)
            if (($k.Key -eq [ConsoleKey]::T) -and (($k.Modifiers -band [ConsoleModifiers]::Control) -ne 0)) { return 99 }
            $action = Get-SdatMenuKeyAction -KeyInfo $k
            if ($action -eq 'select') { return $idx }
            if ($action -eq 'back') { return $null }
            if ($action) {
                $next = Get-SdatMenuTargetIndex -Index $idx -Action $action -Count $options.Count -Grid
                if ($next -ne $idx) {
                    $prev = $idx
                    $idx = $next
                    Render-OptionLine -Top ($layout.OptionsTop + $prev) -Index $prev -Selected $false
                    Render-OptionLine -Top ($layout.OptionsTop + $idx) -Index $idx -Selected $true
                }
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
        "Self-test command",
        "Last self-test summary",
        "Self-test log",
        "Self-test JSONL",
        "Back"
    )
    $idx = 0

    $consoleReady = $true
    try { $null = [Console]::WindowWidth } catch { $consoleReady = $false }

    if (-not $consoleReady) {
        while ($true) {
            Clear-Host
            Write-SdatTuiTitle -Title "SDAT"
            Write-Host ""
            Write-Host "Diagnostics" -ForegroundColor Gray
            Write-Host ""
            Write-NoticeBar -Notice $Notice
            if ($Header) {
                foreach ($line in ($Header -split "`r?`n")) { Write-SdatHeaderLine -Line $line }
                Write-Host ""
            }
            Write-Host ""
            for ($i = 0; $i -lt $options.Count; $i++) {
                $line = Format-SdatCenteredLine -Text $options[$i] -Width 36
                if ($i -eq $idx) { Write-Host $line -ForegroundColor Black -BackgroundColor DarkCyan }
                else { Write-Host $line -ForegroundColor Gray }
            }
            Write-Host ""
            Write-Host (Get-SdatMenuHint -Diagnostics) -ForegroundColor DarkGray

            $k = [Console]::ReadKey($true)
            $action = Get-SdatMenuKeyAction -KeyInfo $k
            if ($action -eq 'select') { return $idx }
            if ($action -eq 'back') { return $null }
            if ($action) { $idx = Get-SdatMenuTargetIndex -Index $idx -Action $action -Count $options.Count }
        }
    }

    $w = Get-ConsoleWidthSafe
    $bg = [Console]::BackgroundColor
    $oldCursor = $true
    try { $oldCursor = [Console]::CursorVisible; [Console]::CursorVisible = $false } catch { }

    $mutedFg = [ConsoleColor]::DarkGray
    $optFg = [ConsoleColor]::Gray
    $selFg = [ConsoleColor]::Black
    $selBg = [ConsoleColor]::DarkCyan

    function Render-OptionLine {
        param(
            [Parameter(Mandatory)][int]$Top,
            [Parameter(Mandatory)][int]$Index,
            [Parameter(Mandatory)][bool]$Selected
        )
        $text = Format-SdatCenteredLine -Text $options[$Index] -Width 36
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
        Write-ConsoleAt -Left 0 -Top $row -Text "SDAT" -ForegroundColor ([ConsoleColor]::Cyan) -BackgroundColor $bg -ClearToEnd
        $row++
        Write-ConsoleAt -Left 0 -Top $row -Text "shutdown at" -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
        $row++
        Write-ConsoleAt -Left 0 -Top $row -Text "Diagnostics" -ForegroundColor ([ConsoleColor]::Gray) -BackgroundColor $bg -ClearToEnd
        $row++
        Write-ConsoleAt -Left 0 -Top $row -Text "" -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
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
            Write-ConsoleAt -Left 0 -Top $row -Text "" -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
            $row++
        }

        if ($Header) {
            foreach ($line in ($Header -split "`r?`n")) {
                Write-SdatHeaderLineAt -Top $row -Line $line -BackgroundColor $bg
                $row++
            }
            Write-ConsoleAt -Left 0 -Top $row -Text "" -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
            $row++
        }

        $row++
        $optionsTop = $row
        for ($i = 0; $i -lt $options.Count; $i++) {
            Render-OptionLine -Top ($optionsTop + $i) -Index $i -Selected ($i -eq $Current)
        }

        $footerTop = $optionsTop + $options.Count + 1
        Write-ConsoleAt -Left 0 -Top $footerTop -Text (Get-SdatMenuHint -Diagnostics) -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
        Write-ConsoleAt -Left 0 -Top ($footerTop + 1) -Text "" -ForegroundColor $mutedFg -BackgroundColor $bg -ClearToEnd
        return [pscustomobject]@{ OptionsTop = $optionsTop }
    }

    try {
        $layout = Render-Frame -Current $idx
        while ($true) {
            $k = [Console]::ReadKey($true)
            $action = Get-SdatMenuKeyAction -KeyInfo $k
            if ($action -eq 'select') { return $idx }
            if ($action -eq 'back') { return $null }
            if ($action) {
                $next = Get-SdatMenuTargetIndex -Index $idx -Action $action -Count $options.Count
                if ($next -ne $idx) {
                    $prev = $idx
                    $idx = $next
                    Render-OptionLine -Top ($layout.OptionsTop + $prev) -Index $prev -Selected $false
                    Render-OptionLine -Top ($layout.OptionsTop + $idx) -Index $idx -Selected $true
                }
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
        [string]$Header = "",
        [string]$EmptyAction = "cancel the current schedule",
        [string]$Examples = "2330  23:30  2h  45m"
    )
    $buf = ""

    $consoleReady = $true
    try { $null = [Console]::WindowWidth } catch { $consoleReady = $false }
    if (-not $consoleReady) {
        while ($true) {
            Clear-Host
            Write-SdatTuiTitle -Title $Title
            Write-Host ""
            if ($Header) {
                foreach ($line in ($Header -split "`r?`n")) { Write-SdatHeaderLine -Line $line }
                Write-Host ""
            }
            Write-Host ""
            Write-Host (Format-SdatCenteredLine -Text $Prompt -Width 34) -ForegroundColor Gray
            Write-Host ""
            $field = ("[ {0,-18} ]" -f $buf)
            Write-Host (Format-SdatCenteredLine -Text $field -Width 24) -ForegroundColor White
            Write-Host ""
            Write-Host (Format-SdatCenteredLine -Text $Examples -Width 30) -ForegroundColor DarkGray
            Write-Host ""
            Write-Host (Format-SdatCenteredLine -Text "Enter save  Esc back" -Width 30) -ForegroundColor DarkGray
            Write-Host (Format-SdatCenteredLine -Text ("Empty Enter: {0}" -f $EmptyAction) -Width 42) -ForegroundColor DarkGray

            $k = [Console]::ReadKey($true)
            switch ($k.Key) {
                'Escape' { return $null }
                'Enter' { return $buf }
                'Backspace' { if ($buf.Length -gt 0) { $buf = $buf.Substring(0, $buf.Length - 1) } }
                default {
                    $c = $k.KeyChar
                    if ($c -and (Test-SdatTuiInputCharacter -Character $c) -and $buf.Length -lt 12) {
                        $buf += $c
                    }
                }
            }
        }
    }

    $oldCursor = $true
    try { $oldCursor = [Console]::CursorVisible; [Console]::CursorVisible = $true } catch { }
    $inputTop = 0
    $inputLeft = 0
    $maxLen = 12
    try {
        [Console]::Clear()
        Write-SdatTuiTitle -Title $Title
        Write-Host ""
        if ($Header) {
            foreach ($line in ($Header -split "`r?`n")) { Write-SdatHeaderLine -Line $line }
            Write-Host ""
        }
        Write-Host (Format-SdatCenteredLine -Text $Prompt -Width 34) -ForegroundColor Gray
        Write-Host ""

        $inputTop = [Console]::CursorTop
        $fieldWidth = 22
        $fieldLeft = Get-SdatCenteredLeft -Width $fieldWidth
        $inputLeft = $fieldLeft + 2
        [Console]::SetCursorPosition($fieldLeft, $inputTop)
        [Console]::Write("[ " + (' ' * ($fieldWidth - 4)) + " ]")
        [Console]::WriteLine("")
        Write-Host ""
        Write-Host (Format-SdatCenteredLine -Text $Examples -Width 30) -ForegroundColor DarkGray
        Write-Host ""
        Write-Host (Format-SdatCenteredLine -Text "Enter save  Esc back" -Width 30) -ForegroundColor DarkGray
        Write-Host (Format-SdatCenteredLine -Text ("Empty Enter: {0}" -f $EmptyAction) -Width 42) -ForegroundColor DarkGray

        $w = Get-ConsoleWidthSafe
        function Render-InputLine {
            param([AllowEmptyString()][string]$Value)
            try {
                [Console]::SetCursorPosition($fieldLeft, $inputTop)
                $out = $Value
                if ($out.Length -gt $maxLen) { $out = $out.Substring(0, $maxLen) }
                $pad = [Math]::Max(0, ($fieldWidth - 4) - $out.Length)
                [Console]::Write("[ " + $out + (' ' * $pad) + " ]")
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
                if ($c -and (Test-SdatTuiInputCharacter -Character $c) -and $buf.Length -lt $maxLen) {
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
