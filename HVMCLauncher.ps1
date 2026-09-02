Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$ErrorActionPreference = 'Stop'
$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$Updater = Join-Path $Root 'HVMCUpdater.ps1'
$UpdaterUrl = 'https://raw.githubusercontent.com/Bendemen-Studios/HVMC/main/HVMCUpdater.ps1'
$LauncherUrls = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Minecraft Launcher\MinecraftLauncher.exe'),
    (Join-Path $env:ProgramFiles 'Minecraft Launcher\MinecraftLauncher.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Minecraft Launcher\MinecraftLauncher.exe')
)
New-Item -ItemType Directory -Force -Path $Root | Out-Null

function Get-MinecraftLauncher {
    foreach ($path in $LauncherUrls) {
        if ($path -and (Test-Path -LiteralPath $path)) { return $path }
    }
    return $null
}

function Ensure-Updater {
    $tmp = "$Updater.download"
    try {
        Invoke-WebRequest -Uri $UpdaterUrl -OutFile $tmp -UseBasicParsing -Headers @{'User-Agent'='HVMC-Launcher'} -TimeoutSec 60
        Move-Item $tmp $Updater -Force
        return $true
    } catch {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        return (Test-Path -LiteralPath $Updater)
    }
}

$script:busy = $false
$script:status = $null

$window = New-Object Windows.Window
$window.Title = 'HVMC'
$window.Width = 520
$window.Height = 720
$window.MinWidth = 460
$window.MinHeight = 620
$window.WindowStartupLocation = 'CenterScreen'
$window.ResizeMode = 'CanResize'
$window.Background = [Windows.Media.BrushConverter]::new().ConvertFromString('#F7F4EC')
$window.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#20231F')
$window.FontFamily = New-Object Windows.Media.FontFamily('Segoe UI')

$rootGrid = New-Object Windows.Controls.Grid
$rootGrid.Margin = New-Object Windows.Thickness(28)
$rootGrid.RowDefinitions.Add((New-Object Windows.Controls.RowDefinition))
$rootGrid.RowDefinitions.Add((New-Object Windows.Controls.RowDefinition))
$rootGrid.RowDefinitions.Add((New-Object Windows.Controls.RowDefinition))
$rootGrid.RowDefinitions.Add((New-Object Windows.Controls.RowDefinition))

$header = New-Object Windows.Controls.StackPanel
$header.HorizontalAlignment = 'Center'
$header.VerticalAlignment = 'Center'

$brand = New-Object Windows.Controls.TextBlock
$brand.Text = 'HVMC'
$brand.FontSize = 68
$brand.FontWeight = 'Bold'
$brand.HorizontalAlignment = 'Center'
$brand.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#B8871F')
$brand.Margin = New-Object Windows.Thickness(0,35,0,0)

$sub = New-Object Windows.Controls.TextBlock
$sub.Text = "HERO'S VAULT"
$sub.FontSize = 16
$sub.FontWeight = 'SemiBold'
$sub.LetterSpacing = 2
$sub.HorizontalAlignment = 'Center'
$sub.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#6F6758')

$header.Children.Add($brand) | Out-Null
$header.Children.Add($sub) | Out-Null
$rootGrid.Children.Add($header) | Out-Null
[Windows.Controls.Grid]::SetRow($header,0)

$status = New-Object Windows.Controls.TextBlock
$status.Text = 'Klaar om te spelen.'
$status.FontSize = 14
$status.HorizontalAlignment = 'Center'
$status.VerticalAlignment = 'Center'
$status.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#6F6758')
$rootGrid.Children.Add($status) | Out-Null
[Windows.Controls.Grid]::SetRow($status,1)

function New-GoldButton([string]$Text) {
    $b = New-Object Windows.Controls.Button
    $b.Content = $Text
    $b.Height = 72
    $b.Margin = New-Object Windows.Thickness(0,10,0,10)
    $b.FontSize = 24
    $b.FontWeight = 'Bold'
    $b.Cursor = [Windows.Input.Cursors]::Hand
    $b.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#241C09')
    $b.Background = [Windows.Media.BrushConverter]::new().ConvertFromString('#D5A62F')
    $b.BorderBrush = [Windows.Media.BrushConverter]::new().ConvertFromString('#B8871F')
    $b.BorderThickness = New-Object Windows.Thickness(1)
    $b.Padding = New-Object Windows.Thickness(20)
    return $b
}

$buttons = New-Object Windows.Controls.StackPanel
$buttons.VerticalAlignment = 'Center'
$buttons.Margin = New-Object Windows.Thickness(70,15,70,15)
$play = New-GoldButton 'SPELEN'
$exit = New-GoldButton 'AFSLUITEN'
$exit.Background = [Windows.Media.BrushConverter]::new().ConvertFromString('#FFFFFF')
$exit.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#4D4639')
$buttons.Children.Add($play) | Out-Null
$buttons.Children.Add($exit) | Out-Null
$rootGrid.Children.Add($buttons) | Out-Null
[Windows.Controls.Grid]::SetRow($buttons,2)

$footer = New-Object Windows.Controls.TextBlock
$footer.Text = 'Bendemen Studios'
$footer.FontSize = 12
$footer.HorizontalAlignment = 'Center'
$footer.VerticalAlignment = 'Center'
$footer.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#9A907D')
$rootGrid.Children.Add($footer) | Out-Null
[Windows.Controls.Grid]::SetRow($footer,3)

$window.Content = $rootGrid

$play.Add_Click({
    if ($script:busy) { return }
    $script:busy = $true
    $play.IsEnabled = $false
    $exit.IsEnabled = $false
    $status.Text = 'HVMC voorbereiden...'
    try {
        if (-not (Ensure-Updater)) { throw 'HVMC updater kon niet worden gedownload.' }
        $status.Text = 'Updates controleren en bestanden bijwerken...'
        $proc = Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$Updater) -Wait -PassThru -WindowStyle Hidden
        if ($proc.ExitCode -ne 0) { throw "Updater afgesloten met foutcode $($proc.ExitCode)." }
        $launcher = Get-MinecraftLauncher
        if (-not $launcher) { throw 'De officiële Minecraft Launcher is niet gevonden. Start de HVMC updater/installatie opnieuw.' }
        $status.Text = 'Minecraft wordt gestart...'
        Start-Process -FilePath $launcher | Out-Null
        $status.Text = 'Minecraft Launcher gestart.'
    } catch {
        $status.Text = $_.Exception.Message
        [Windows.MessageBox]::Show($window,$_.Exception.Message,'HVMC','OK','Error') | Out-Null
    } finally {
        $script:busy = $false
        $play.IsEnabled = $true
        $exit.IsEnabled = $true
    }
})

$exit.Add_Click({ $window.Close() })
$window.Add_MouseLeftButtonDown({ if ($_.ButtonState -eq 'Pressed') { $window.DragMove() } })

[void]$window.ShowDialog()
