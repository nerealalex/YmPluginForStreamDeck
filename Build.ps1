$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Сначала установи .NET SDK 10."
}

if (Get-Process -Name StreamDeck -ErrorAction SilentlyContinue) {
    throw "Полностью закрой Stream Deck через значок в трее и повтори сборку."
}

$id = "com.local.ymdeck"
$plugin = Join-Path $PSScriptRoot "$id.sdPlugin"
$bin = Join-Path $plugin "bin"
$images = Join-Path $plugin "images"

New-Item -ItemType Directory -Force -Path $bin | Out-Null
New-Item -ItemType Directory -Force -Path $images | Out-Null

dotnet publish ".\YmDeck.csproj" -c Release -r win-x64 --self-contained true -o $bin

if ($LASTEXITCODE -ne 0) {
    throw "Сборка не завершена. Посмотри сообщение компилятора выше."
}

Add-Type -AssemblyName System.Drawing

function New-Icon {
    param(
        [string]$Name,
        [string]$Text,
        [int]$Size,
        [string]$Suffix = ""
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    $graphics.SmoothingMode = "AntiAlias"
    $graphics.TextRenderingHint = "AntiAliasGridFit"

    $background = [System.Drawing.Color]::FromArgb(25, 25, 30)
    $foreground = [System.Drawing.Color]::FromArgb(255, 210, 0)

    $graphics.Clear($background)

    $font = [System.Drawing.Font]::new(
        "Segoe UI", [float]($Size / 5),
        [System.Drawing.FontStyle]::Bold,
        [System.Drawing.GraphicsUnit]::Pixel
    )

    $brush = [System.Drawing.SolidBrush]::new($foreground)
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = "Center"
    $format.LineAlignment = "Center"

    $rect = [System.Drawing.RectangleF]::new(0, 0, $Size, $Size)
    $graphics.DrawString($Text, $font, $brush, $rect, $format)

    $path = Join-Path $images "$Name$Suffix.png"
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)

    $format.Dispose()
    $brush.Dispose()
    $font.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

$items = @(
    @{ Key = "previous"; Name = "Предыдущий трек"; Text = "PREV" },
    @{ Key = "playpause"; Name = "Пауза / играть"; Text = "PLAY" },
    @{ Key = "next"; Name = "Следующий трек"; Text = "NEXT" },
    @{ Key = "track"; Name = "Трек и обложка"; Text = "MUSIC" },
    @{ Key = "like"; Name = "Лайк"; Text = "LIKE" },
    @{ Key = "dislike"; Name = "Дизлайк"; Text = "NO" }
)

New-Icon -Name "plugin" -Text "YM" -Size 256
New-Icon -Name "action" -Text "YM" -Size 20
New-Icon -Name "action" -Text "YM" -Size 40 -Suffix "@2x"

$actions = @(
    foreach ($item in $items) {
        New-Icon -Name $item.Key -Text $item.Text -Size 72
        New-Icon -Name $item.Key -Text $item.Text -Size 144 -Suffix "@2x"

        @{
            UUID = "$id.$($item.Key)"
            Name = $item.Name
            Icon = "images/action"
            Controllers = @("Keypad")
            SupportedInMultiActions = $false
            States = @(
                @{
                    Image = "images/$($item.Key)"
                    TitleAlignment = "bottom"
                    FontSize = 10
                    FontStyle = "Bold"
                    TitleColor = "#FFFFFF"
                }
            )
        }
    }
)

$manifest = @{
    UUID = $id
    Name = "Яндекс Музыка — локальное управление"
    Author = "Local"
    Description = "Управление приложением Яндекс Музыки в Windows."
    Version = "0.1.0.0"
    SDKVersion = 2
    CodePath = "bin/YmDeck.exe"
    Icon = "images/plugin"
    Category = "Яндекс Музыка"
    CategoryIcon = "images/action"
    Software = @{
        MinimumVersion = "6.4"
    }
    OS = @(
        @{
            Platform = "windows"
            MinimumVersion = "10"
        }
    )
    Actions = $actions
}

$manifestPath = Join-Path $plugin "manifest.json"
$json = $manifest | ConvertTo-Json -Depth 12

[System.IO.File]::WriteAllText(
    $manifestPath,
    $json,
    [System.Text.UTF8Encoding]::new($false)
)

$destination = Join-Path $env:APPDATA "Elgato\StreamDeck\Plugins\$id.sdPlugin"

New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item (Join-Path $plugin "*") $destination -Recurse -Force

Write-Host ""
Write-Host "Сборка и копирование завершены."
Write-Host "Запусти Stream Deck и найди раздел «Яндекс Музыка»."
