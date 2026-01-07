Set-StrictMode -Version Latest

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

function Show-SdatMainMenu {
    param(
        [Parameter(Mandatory)][string]$Title,
        [string]$Header = "",
        $Notice
    )
    $options = @(
        "One-time (volatile)",
        "Daily (permanent)"
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
            if (($k.Key -eq [ConsoleKey]::T) -and (($k.Modifiers -band [ConsoleModifiers]::Control) -ne 0)) { return 99 }
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
            Write-Host ("> " + $buf) -ForegroundColor White
            Write-Host ""
            Write-Host "Type HHMM (digits only). Enter=confirm | Esc=back | Empty+Enter=cancel" -ForegroundColor DarkGray

            $k = [Console]::ReadKey($true)
            switch ($k.Key) {
                'Escape' { return $null }
                'Enter' { return $buf }
                'Backspace' { if ($buf.Length -gt 0) { $buf = $buf.Substring(0, $buf.Length - 1) } }
                default {
                    $c = $k.KeyChar
                    if ($c -and [char]::IsDigit($c) -and $buf.Length -lt 4) { $buf += $c }
                }
            }
        }
    }

    $oldCursor = $true
    try { $oldCursor = [Console]::CursorVisible; [Console]::CursorVisible = $false } catch { }
    $inputTop = 0
    $inputLeft = 0
    $maxLen = 4
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
        Write-Host "Type HHMM (digits only). Enter=confirm | Esc=back | Empty+Enter=cancel" -ForegroundColor DarkGray

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
                    if ($c -and [char]::IsDigit($c) -and $buf.Length -lt $maxLen) {
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
