# 一键打包：构建自包含 Release，整理出干净的 Mica 文件夹并压成 dist\Mica.zip
#
# 为什么不用 dotnet publish：未打包(unpackaged)的 WinUI 3 用 publish 会丢掉 Mica.pri
# (XAML 资源索引) 和 Assets\Editor (编辑器本体)，导致 exe 启动即在原生层崩溃、双击无反应。
# 所以直接拿「自包含 build 的输出目录」当交付物——它已含 PRI、编辑器资源、.NET 运行时，
# 且 csproj 设了 WindowsAppSDKSelfContained=true，连 Windows App Runtime 也打进去了，
# 纯净电脑双击即用、无需安装任何组件。
#
# 用法（仓库根目录或任意位置均可）：
#   powershell -ExecutionPolicy Bypass -File build\pack.ps1

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$proj     = Join-Path $repoRoot "Mica\Mica.csproj"
$rid      = "win-x64"
$config   = "Release"

$buildOut = Join-Path $repoRoot "Mica\bin\x64\$config\net8.0-windows10.0.19041.0\$rid"
$distRoot = Join-Path $repoRoot "dist"
$appDir   = Join-Path $distRoot "Mica"      # 压缩后顶层就是 Mica\，不会多出一层 win-x64
$zipPath  = Join-Path $distRoot "Mica.zip"

Write-Host "==> 构建自包含 Release ($rid)，请稍候..." -ForegroundColor Cyan
# -r + --self-contained 才会产出带 .NET 运行时 + WinAppSDK 运行时的完整 win-x64 目录
dotnet build $proj -c $config -p:Platform=x64 -r $rid --self-contained true
if ($LASTEXITCODE -ne 0) { throw "构建失败，已中止。" }

if (-not (Test-Path (Join-Path $buildOut "Mica.exe"))) {
    throw "未找到构建输出：$buildOut\Mica.exe"
}
# 校验关键文件，防止又出现「缺 PRI/编辑器」的坏包
if (-not (Test-Path (Join-Path $buildOut "Mica.pri"))) {
    throw "构建输出缺少 Mica.pri（XAML 资源索引），打出来会打不开。已中止。"
}
if (-not (Test-Path (Join-Path $buildOut "Assets\Editor\index.html"))) {
    throw "构建输出缺少 Assets\Editor（编辑器资源）。请先确认 web 已构建。已中止。"
}

Write-Host "==> 整理交付目录..." -ForegroundColor Cyan
if (Test-Path $distRoot) { Remove-Item -Recurse -Force $distRoot }
New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

# 把 win-x64 整个复制为 Mica\：解压后是 Mica\Mica.exe，没有 win-x64 这层
Copy-Item -Recurse -Force $buildOut $appDir

# 删掉分发不需要的：调试符号(.pdb)、以及万一残留的坏 publish 子目录
Get-ChildItem -Path $appDir -Recurse -Include *.pdb | Remove-Item -Force
$badPublish = Join-Path $appDir "publish"
if (Test-Path $badPublish) { Remove-Item -Recurse -Force $badPublish }

Write-Host "==> 压缩为 Mica.zip..." -ForegroundColor Cyan
Compress-Archive -Path $appDir -DestinationPath $zipPath -Force

$sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "完成：$zipPath ($sizeMB MB)" -ForegroundColor Green
Write-Host "解压后结构：Mica\Mica.exe —— 双击即用，无需安装运行时。" -ForegroundColor Green

