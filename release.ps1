<#
  DSBinder — випуск нової версії однією командою.

  Використання (PowerShell, з папки D:\Projects\DSBinder):
    .\release.ps1                  # версія +1 (2.0.0 -> 2.0.1), збірка, інсталятор, публікація на GitHub
    .\release.ps1 -Version 2.1.0   # конкретна версія
    .\release.ps1 -NoUpload        # лише зібрати інсталятор локально (папка Releases), нічого не публікувати

  Токен GitHub (для публікації): змінна середовища DSBINDER_GH_TOKEN або параметр -Token.
  Результат: Releases\DSBinder-win-Setup.exe — його і даєте людям.
#>
param(
    [string]$Version,
    [string]$Token = $env:DSBINDER_GH_TOKEN,
    [switch]$NoUpload
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$projDir  = Join-Path $PSScriptRoot 'DSBinder'
$csproj   = Join-Path $projDir 'DSBinder.csproj'
$appInfo  = Join-Path $projDir 'Core\AppInfo.cs'
$icon     = Join-Path $projDir 'Assets\DSBinder.ico'
$publish  = Join-Path $PSScriptRoot 'publish'
$releases = Join-Path $PSScriptRoot 'Releases'

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Yellow }
function Fail([string]$text) { Write-Host "`nПОМИЛКА: $text" -ForegroundColor Red; exit 1 }

# ---------- 1. Репозиторій для оновлень ----------
$repo = ([regex]::Match((Get-Content $appInfo -Raw), 'UpdateRepoUrl\s*=\s*"([^"]+)"')).Groups[1].Value
if (-not $NoUpload) {
    if (-not $repo -or $repo -match 'YOUR_GITHUB_NAME') {
        Fail "Вкажіть свій репозиторій у DSBinder\Core\AppInfo.cs (UpdateRepoUrl) або запустіть з -NoUpload."
    }
    if (-not $Token) {
        $secure = Read-Host "Токен GitHub (не відображається)" -AsSecureString
        $Token = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
        if (-not $Token) { Fail "Без токена GitHub опублікувати реліз неможливо." }
    }
}

# ---------- 2. Версія ----------
$xml = Get-Content $csproj -Raw
$current = ([regex]::Match($xml, '<Version>([^<]+)</Version>')).Groups[1].Value
if (-not $Version) {
    $parts = $current.Split('.')
    $parts[2] = [string]([int]$parts[2] + 1)
    $Version = $parts -join '.'
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "Версія має бути у форматі 1.2.3 (отримано: $Version)" }

Step "Версія: $current -> $Version"
$xml = [regex]::Replace($xml, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText($csproj, $xml, (New-Object System.Text.UTF8Encoding($true)))

# ---------- 3. vpk ----------
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Step "Встановлення vpk (інструмент Velopack)"
    dotnet tool install -g vpk
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { Fail "vpk не встановився. Виконайте вручну: dotnet tool install -g vpk" }
}

# ---------- 4. Збірка ----------
Step "Збірка (Release, win-x64, self-contained)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { Fail "Збірка не вдалася — дивіться помилки вище." }

# ---------- 5. Попередній реліз (для маленьких дельта-оновлень) ----------
if (-not $NoUpload) {
    Step "Завантаження попереднього релізу з GitHub"
    vpk download github --repoUrl $repo --token $Token --outputDir $releases
    if ($LASTEXITCODE -ne 0) { Write-Host "Попередніх релізів немає — це нормально для першого випуску." -ForegroundColor DarkGray }
}

# ---------- 6. Пакування ----------
Step "Створення інсталятора та пакета оновлення"
vpk pack --packId DSBinder --packVersion $Version --packDir $publish --mainExe DSBinder.exe `
         --packTitle "DSBinder" --packAuthors "DSBinder" --icon $icon --outputDir $releases
if ($LASTEXITCODE -ne 0) { Fail "vpk pack не вдалося." }

# ---------- 7. Публікація ----------
if ($NoUpload) {
    Step "Готово (без публікації)"
    Write-Host "Інсталятор: $releases\DSBinder-win-Setup.exe" -ForegroundColor Green
    exit 0
}

Step "Публікація релізу v$Version на GitHub"
vpk upload github --repoUrl $repo --token $Token --outputDir $releases --publish `
                  --releaseName "DSBinder $Version" --tag "v$Version"
if ($LASTEXITCODE -ne 0) { Fail "Публікація не вдалася. Перевірте токен (потрібен доступ Contents: Read and write)." }

Write-Host "`nГотово! Версія $Version опублікована." -ForegroundColor Green
Write-Host "Інсталятор для нових користувачів: $releases\DSBinder-win-Setup.exe"
Write-Host "Встановлені копії DSBinder отримають оновлення автоматично."
