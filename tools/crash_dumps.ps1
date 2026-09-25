<#
.SYNOPSIS
    Включает, показывает или выключает запись дампов при падении Torch-сервера (Windows Error Reporting, LocalDumps).

.DESCRIPTION
    Windows сама пишет дамп процесса, когда он падает с необработанным исключением: нативный stack overflow,
    access violation и т. п. - то, после чего в логе Torch ничего нет, а в журнале событий только
    "Application Error ... Havok.dll". Скрипт настраивает это для Torch.Server.exe. Пока сервер не падает,
    это ничего не стоит.

    Нужен запуск от администратора (ключ лежит в HKLM). Перезапуск сервера не нужен.

    Дамп - это вся память процесса, включая токены, пароли и ключи из конфигов: храните его закрыто.
    Пока дамп пишется, рядом работает WerFault.exe, а процесс висит; сторож, который перезапускает сервер,
    не должен убивать ни его, ни WerFault раньше, чем WerFault завершится.

.PARAMETER Folder
    Куда писать дампы. По умолчанию C:\SE\CrashDumps.

.PARAMETER Count
    Сколько последних дампов хранить (старые Windows удаляет сама). По умолчанию 5.

.PARAMETER Mini
    Мини-дамп вместо полного. Он маленький, но в нём не будет управляемых стеков: будет видно только
    "упало в Havok.dll", без того, какой метод игры или плагина туда привёл. По умолчанию - полный.

.PARAMETER Process
    Имя исполняемого файла. По умолчанию Torch.Server.exe.

.PARAMETER Disable
    Выключить (удалить настройку).

.PARAMETER Status
    Только показать текущую настройку и место на диске.

.EXAMPLE
    .\crash_dumps.ps1                                  # включить, полные дампы в C:\SE\CrashDumps, 5 штук
    .\crash_dumps.ps1 -Folder D:\CrashDumps -Count 3
    .\crash_dumps.ps1 -Status
    .\crash_dumps.ps1 -Disable

.NOTES
    Разбор дампа: dumpstacks.exe <файл.dmp> - поток с исключением и управляемые стеки всех потоков
    (инструмент собран под .NET 8; на машине с одним .NET 9 запускать с DOTNET_ROLL_FORWARD=Major).
#>
param(
    [string]$Folder = 'C:\SE\CrashDumps',
    [ValidateRange(1, 100)][int]$Count = 5,
    [switch]$Mini,
    [string]$Process = 'Torch.Server.exe',
    [switch]$Disable,
    [switch]$Status
)

$ErrorActionPreference = 'Stop'
$root = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps'
$key = Join-Path $root $Process

function Show-State {
    if (-not (Test-Path $key)) {
        Write-Host "Дампы при падении $Process выключены (нет ключа $key)."
        return
    }
    $v = Get-ItemProperty -Path $key
    $type = switch ($v.DumpType) { 1 { 'мини' } 2 { 'полный' } 0 { 'custom' } default { "не задан (мини)" } }
    Write-Host "Дампы при падении $Process включены:"
    Write-Host "  папка:     $($v.DumpFolder)"
    Write-Host "  тип:       $type"
    Write-Host "  хранится:  $($v.DumpCount)"
    $dir = [Environment]::ExpandEnvironmentVariables([string]$v.DumpFolder)
    if ($dir -and (Test-Path $dir)) {
        $drive = Get-PSDrive -Name ($dir.Substring(0, 1)) -ErrorAction SilentlyContinue
        if ($drive) { Write-Host ("  свободно:  {0:N0} ГБ на диске {1}:" -f ($drive.Free / 1GB), $drive.Name) }
        $dumps = Get-ChildItem -Path $dir -Filter '*.dmp' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
        if ($dumps) {
            Write-Host "  дампы:"
            $dumps | Select-Object -First 10 | ForEach-Object { Write-Host ("    {0}  {1,8:N1} ГБ  {2}" -f $_.LastWriteTime.ToString('yyyy-MM-dd HH:mm'), ($_.Length / 1GB), $_.Name) }
        }
    }
    # WER может быть выключен целиком политикой - тогда дампов не будет и с этим ключом
    $werOff = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting' -Name Disabled -ErrorAction SilentlyContinue).Disabled
    $policyOff = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting' -Name Disabled -ErrorAction SilentlyContinue).Disabled
    if ($werOff -eq 1 -or $policyOff -eq 1) {
        Write-Warning "Windows Error Reporting выключен (Disabled = 1): дампы писаться не будут."
    }
}

if ($Status) { Show-State; return }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Нужен запуск от администратора: настройка лежит в HKLM.' }

if ($Disable) {
    if (Test-Path $key) { Remove-Item -Path $key -Recurse -Force; Write-Host "Дампы при падении $Process выключены." }
    else { Write-Host "Уже выключено." }
    return
}

New-Item -ItemType Directory -Path $Folder -Force | Out-Null
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name DumpFolder -Value $Folder -PropertyType ExpandString -Force | Out-Null
New-ItemProperty -Path $key -Name DumpType -Value ($(if ($Mini) { 1 } else { 2 })) -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name DumpCount -Value $Count -PropertyType DWord -Force | Out-Null

# полный дамп - примерно столько, сколько памяти занимает сервер: предупредить, если места не хватит на все
if (-not $Mini) {
    $running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Process)) -ErrorAction SilentlyContinue | Select-Object -First 1
    $drive = Get-PSDrive -Name ($Folder.Substring(0, 1)) -ErrorAction SilentlyContinue
    if ($running -and $drive -and $drive.Free -lt $running.PrivateMemorySize64 * $Count) {
        Write-Warning ("Сервер сейчас занимает {0:N1} ГБ, на {1} дампов нужно ~{2:N0} ГБ, а свободно {3:N0} ГБ." -f `
            ($running.PrivateMemorySize64 / 1GB), $Count, ($running.PrivateMemorySize64 * $Count / 1GB), ($drive.Free / 1GB))
    }
}

Show-State
