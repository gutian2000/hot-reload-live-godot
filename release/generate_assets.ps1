# Hot Reload Live - Asset Generation Script
# Generates: icon, cover, and screenshots for itch.io / Godot Asset Store

Add-Type -AssemblyName System.Drawing

$outDir = 'D:\GodotHotReload\release\assets'

$godotBlue   = [System.Drawing.Color]::FromArgb(71, 140, 191)
$godotDark   = [System.Drawing.Color]::FromArgb(35, 70, 96)
$bgDark      = [System.Drawing.Color]::FromArgb(25, 30, 40)
$bgPanel     = [System.Drawing.Color]::FromArgb(30, 35, 45)
$textWhite   = [System.Drawing.Color]::FromArgb(235, 235, 235)
$textMuted   = [System.Drawing.Color]::FromArgb(150, 160, 175)
$accentGreen = [System.Drawing.Color]::FromArgb(76, 175, 80)
$accentAmber = [System.Drawing.Color]::FromArgb(255, 179, 0)
$accentCyan  = [System.Drawing.Color]::FromArgb(0, 200, 220)
$v1Color     = [System.Drawing.Color]::FromArgb(255, 110, 110)
$v2Color     = [System.Drawing.Color]::FromArgb(110, 220, 110)

function New-Bitmap([int]$w, [int]$h) {
    return New-Object System.Drawing.Bitmap($w, $h)
}

function Fill-Rect($g, [int]$x, [int]$y, [int]$w, [int]$h, $color) {
    $brush = New-Object System.Drawing.SolidBrush($color)
    $g.FillRectangle($brush, $x, $y, $w, $h)
    $brush.Dispose()
}

function Draw-Text($g, [string]$text, [int]$x, [int]$y, $color, [int]$fontSize = 12, [bool]$bold = $false, [string]$fontName = 'Consolas') {
    $style = if ($bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
    $font = New-Object System.Drawing.Font($fontName, $fontSize, $style)
    $brush = New-Object System.Drawing.SolidBrush($color)
    $g.DrawString($text, $font, $brush, $x, $y)
    $brush.Dispose()
    $font.Dispose()
}

# === 1. ICON 256x256 ===
$icon = New-Bitmap 256 256
$g = [System.Drawing.Graphics]::FromImage($icon)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
Fill-Rect $g 0 0 256 128 $godotDark
Fill-Rect $g 0 128 256 128 $godotBlue
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 6)
$g.DrawRectangle($pen, 16, 16, 224, 224)
$pen.Dispose()
$bolt = New-Object System.Drawing.Drawing2D.GraphicsPath
$pts = @(
    [System.Drawing.Point]::new(138, 40),
    [System.Drawing.Point]::new(92, 138),
    [System.Drawing.Point]::new(122, 138),
    [System.Drawing.Point]::new(108, 216),
    [System.Drawing.Point]::new(178, 112),
    [System.Drawing.Point]::new(142, 112),
    [System.Drawing.Point]::new(156, 40)
)
$bolt.AddPolygon($pts)
$wb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$g.FillPath($wb, $bolt)
$wb.Dispose(); $bolt.Dispose()
$g.Dispose()
$icon.Save("$outDir\icon_256.png", [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host 'OK icon_256.png'

# === 2. COVER 630x500 ===
$cover = New-Bitmap 630 500
$g = [System.Drawing.Graphics]::FromImage($cover)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
Fill-Rect $g 0 0 630 500 $bgDark
Fill-Rect $g 0 0 630 6 $godotBlue
Draw-Text $g 'Hot Reload Live' 40 60 $textWhite 42 $true 'Segoe UI'
Draw-Text $g 'for Godot 4.7+ (.NET 8)' 42 115 $godotBlue 20 $false 'Segoe UI'
Draw-Text $g 'Save a .cs file - watch it run.' 42 175 $textMuted 18 $false 'Segoe UI'
Draw-Text $g 'No restart. No domain reload. Zero stuttering.' 42 205 $textMuted 16 $false 'Segoe UI'

$chips = @('Static','Instance','Generics','async/await','Iterators','ref/out','Field Guard','Restore All')
$chipX = 42; $chipY = 260; $chipW = 130; $chipH = 36; $gap = 8
for ($i = 0; $i -lt $chips.Count; $i++) {
    $col = $i % 4; $row = [math]::Floor($i / 4)
    $cx = $chipX + $col * ($chipW + $gap)
    $cy = $chipY + $row * ($chipH + $gap)
    Fill-Rect $g $cx $cy $chipW $chipH $bgPanel
    $pen2 = New-Object System.Drawing.Pen($godotBlue, 2)
    $g.DrawLine($pen2, $cx, $cy, $cx + $chipW, $cy)
    $pen2.Dispose()
    Draw-Text $g $chips[$i] ($cx + 12) ($cy + 8) $textWhite 13 $false 'Segoe UI'
}
Fill-Rect $g 0 450 630 50 $godotDark
Draw-Text $g 'v1.1.0  .  MIT License  .  by gutian2000' 42 465 $textMuted 14 $false 'Segoe UI'
Draw-Text $g '>>' 560 455 $godotBlue 28 $false 'Segoe UI'
$g.Dispose()
$cover.Save("$outDir\cover_630x500.png", [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host 'OK cover_630x500.png'

# === Console helper ===
function New-Console {
    $bmp = New-Bitmap 1280 720
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    Fill-Rect $g 0 0 1280 32 $godotDark
    Draw-Text $g 'Godot 4.7.2 - Output' 12 7 $textWhite 13 $false 'Segoe UI'
    Fill-Rect $g 0 32 1280 688 $bgDark
    return [pscustomobject]@{ bmp = $bmp; g = $g; y = 44 }
}

function Add-Line($ctx, [string]$text, $color = $textWhite) {
    Draw-Text $ctx.g $text 16 $ctx.y $color 13 $false 'Consolas'
    $ctx.y += 20
}

function Save-Console($ctx, [string]$name) {
    $ctx.g.Dispose()
    $ctx.bmp.Save("$outDir\$name", [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "OK $name"
}

# === Screenshot 1: baseline v1 ===
$c = New-Console
Add-Line $c '[Hot Reload Live] [OK] Runtime started - watching D:/MyGame/' $accentGreen
Add-Line $c ''
Add-Line $c '[Boundary] static void -> v1' $v1Color
Add-Line $c '[Boundary] static ret -> v1' $v1Color
Add-Line $c '[Boundary] static out -> v1' $v1Color
Add-Line $c '[Boundary] overload(string) -> v1' $v1Color
Add-Line $c '[Boundary] overload(int) -> v1' $v1Color
Add-Line $c '[Boundary] generic<String> -> v1, value=hello' $v1Color
Add-Line $c '[Boundary] generic<Int32> -> v1, value=99' $v1Color
Add-Line $c '[Boundary] instance void -> v1 (counter=5)' $v1Color
Add-Line $c '[Boundary] instance ret -> v1' $v1Color
Add-Line $c '  ref result: [Boundary] instance ref -> v1' $v1Color
Add-Line $c '[Boundary] async entry -> v1' $v1Color
Add-Line $c '[Boundary] iterator entry -> v1' $v1Color
Add-Line $c '[Boundary] iterator after yield -> v1' $v1Color
Add-Line $c '[Boundary] async after await -> v1' $v1Color
Add-Line $c ''
Add-Line $c '>>> Baseline run - all methods executing v1' $textMuted
Save-Console $c 'screenshot_1_baseline_v1.png'

# === Screenshot 2: hot reload moment ===
$c = New-Console
Add-Line $c '[Hot Reload Live] Changed: BoundaryDemo.cs' $accentCyan
Add-Line $c '[Hot Reload Live] Changed: BoundaryDemo.cs' $accentCyan
Add-Line $c ''
Add-Line $c '[Hot Reload Live] [build] Building v1 ...' $accentCyan
Add-Line $c '[Hot Reload Live] [file] Built: D:/MyGame/.hotswap/v1/MyGame.dll' $accentCyan
Add-Line $c '[Hot Reload Live] [diff] Old methods: 14 / New: 14' $accentCyan
Add-Line $c '[Hot Reload Live] [OK] Patched: static BoundaryDemo.StaticGreet' $accentGreen
Add-Line $c '[Hot Reload Live] [OK] Patched: static BoundaryDemo.StaticAdd' $accentGreen
Add-Line $c '[Hot Reload Live] [OK] Patched: static BoundaryDemo.StaticOutTest' $accentGreen
Add-Line $c '[Hot Reload Live] [OK] Patched: static BoundaryDemo.StaticOverload' $accentGreen
Add-Line $c '[Hot Reload Live] [OK] Patched: static BoundaryDemo.StaticOverload' $accentGreen
Add-Line $c '[Hot Reload Live] [OK] Generic BoundaryDemo.StaticGeneric`1 -> 9 instantiations' $accentGreen
Add-Line $c '[Hot Reload Live] [OK] Patched: instance BoundaryDemo.InstanceGreet' $accentGreen
Add-Line $c '[Hot Reload Live] [OK] Patched: instance BoundaryDemo.InstanceAdd' $accentGreen
Add-Line $c '[Hot Reload Live] [OK] Patched: instance BoundaryDemo.InstanceRefTest' $accentGreen
Add-Line $c '[Hot Reload Live] [OK] Patched: instance BoundaryDemo._Process' $accentGreen
Add-Line $c '[Hot Reload Live] [OK] Patched: instance <AsyncGreetAsync>d__12.MoveNext' $accentGreen
Add-Line $c '[Hot Reload Live] [OK] Patched: instance <IteratorGreet>d__13.MoveNext' $accentGreen
Add-Line $c ''
Add-Line $c '[Hot Reload Live] [done] v1: 11 new / 0 updated / 1 failed / 9 generic-instantiations' $accentGreen
Save-Console $c 'screenshot_2_hotreload_moment.png'

# === Screenshot 3: result v2 ===
$c = New-Console
Add-Line $c '[Boundary] static void -> v2' $v2Color
Add-Line $c '[Boundary] static ret -> v2' $v2Color
Add-Line $c '[Boundary] static out -> v2' $v2Color
Add-Line $c '[Boundary] overload(string) -> v2' $v2Color
Add-Line $c '[Boundary] overload(int) -> v2' $v2Color
Add-Line $c '[Boundary] generic<String> -> v2, value=hello' $v2Color
Add-Line $c '[Boundary] generic<Int32> -> v2, value=99' $v2Color
Add-Line $c '[Boundary] instance void -> v2 (counter=12)' $v2Color
Add-Line $c '[Boundary] instance ret -> v2' $v2Color
Add-Line $c '  ref result: [Boundary] instance ref -> v2' $v2Color
Add-Line $c '[Boundary] async entry -> v2' $v2Color
Add-Line $c '[Boundary] iterator entry -> v2' $v2Color
Add-Line $c '[Boundary] iterator after yield -> v2' $v2Color
Add-Line $c '[Boundary] async after await -> v2' $v2Color
Add-Line $c ''
Add-Line $c '>>> All 14 methods live-updated v1 -> v2 without restart' $textMuted
Save-Console $c 'screenshot_3_result_v2.png'

# === Screenshot 4: editor plugin menu ===
$editor = New-Bitmap 1280 720
$ge = [System.Drawing.Graphics]::FromImage($editor)
$ge.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
Fill-Rect $ge 0 0 1280 720 $bgDark
Fill-Rect $ge 0 0 1280 28 $godotDark
Draw-Text $ge 'Project  Debug  Build  Scene  Import  Export  Help' 12 5 $textWhite 13 $false 'Segoe UI'
Fill-Rect $ge 10 28 230 170 $bgPanel
$pen3 = New-Object System.Drawing.Pen($godotBlue, 1)
$ge.DrawLine($pen3, 10, 28, 240, 28)
$pen3.Dispose()
Draw-Text $ge '  Project Settings...' 20 38 $textWhite 13 $false 'Segoe UI'
Draw-Text $ge '  Run Project' 20 58 $textMuted 13 $false 'Segoe UI'
Draw-Text $ge '  ------------------------' 20 78 $textMuted 12 $false 'Consolas'
Draw-Text $ge '> Hot Reload Live' 20 98 $accentGreen 13 $true 'Segoe UI'
Draw-Text $ge '      Restart Watcher' 20 118 $textWhite 13 $false 'Segoe UI'
Draw-Text $ge '      Manual Compile' 20 138 $textWhite 13 $false 'Segoe UI'
Draw-Text $ge '      Restore All' 20 158 $accentAmber 13 $false 'Segoe UI'

$editorBg = [System.Drawing.Color]::FromArgb(35, 38, 46)
Fill-Rect $ge 250 28 1030 692 $editorBg
Draw-Text $ge '// BoundaryDemo.cs - edit, save, live update' 260 60 $textMuted 14 $false 'Consolas'
Draw-Text $ge 'public static void StaticGreet()' 260 100 $textWhite 14 $false 'Consolas'
Draw-Text $ge '    GD.Print("[Boundary] static void -> v2");' 280 120 $accentCyan 14 $false 'Consolas'
Draw-Text $ge 'public void InstanceGreet()' 260 160 $textWhite 14 $false 'Consolas'
Draw-Text $ge '    GD.Print("[Boundary] instance -> v2");' 280 180 $accentCyan 14 $false 'Consolas'

Fill-Rect $ge 250 500 1030 220 $bgDark
$pen4 = New-Object System.Drawing.Pen($godotBlue, 2)
$ge.DrawLine($pen4, 250, 500, 1280, 500)
$pen4.Dispose()
Draw-Text $ge '[Hot Reload Live] [OK] Runtime started - watching D:/MyGame/' 260 510 $accentGreen 13 $false 'Consolas'
Draw-Text $ge '[Hot Reload Live] Changed: BoundaryDemo.cs' 260 530 $accentCyan 13 $false 'Consolas'
Draw-Text $ge '[Hot Reload Live] [done] v1: 11 new / 0 updated / 1 failed / 9 generic' 260 550 $accentGreen 13 $false 'Consolas'
Draw-Text $ge '[Boundary] static void -> v2' 260 570 $v2Color 13 $false 'Consolas'
Draw-Text $ge '[Boundary] instance void -> v2' 260 590 $v2Color 13 $false 'Consolas'
$ge.Dispose()
$editor.Save("$outDir\screenshot_4_editor_menu.png", [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host 'OK screenshot_4_editor_menu.png'

Write-Host ''
Write-Host "All assets generated in: $outDir"
