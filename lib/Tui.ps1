Set-StrictMode -Version Latest

function Get-ConsoleWidthSafe {
    try {
        $w = [Console]::WindowWidth
        if ($w -gt 0) { return $w }
    } catch { }
    return 100
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
    while ($true) {
        Clear-Host
        Write-Host $Title -ForegroundColor Cyan
        Write-Hr
        Write-NoticeBar -Notice $Notice
        if ($Header) { Write-Host $Header -ForegroundColor DarkGray; Write-Hr }
        Write-Host ""
        for ($i = 0; $i -lt $options.Count; $i++) {
            if ($i -eq $idx) {
                Write-Host ("> " + $options[$i]) -ForegroundColor Black -BackgroundColor DarkCyan
            } else {
                Write-Host ("  " + $options[$i]) -ForegroundColor Gray
            }
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
            param([Parameter(Mandatory)][string]$Value)
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
