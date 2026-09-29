<#
.SYNOPSIS
    Builds the complete D.A.R.K.S.T.A.R. Windows installer in one go.

.DESCRIPTION
    Takes this source tree and produces installer\output\DARKSTAR-Setup-<version>.exe, which can
    be copied to any Windows machine and installs the bot, the config GUI, a Vosk model and every
    runtime dependency (.NET 8 Desktop Runtime, Visual C++ Redistributable, WebView2 Runtime) -
    each of them skipped on the target machine if it is already present.

    Steps performed:
      1. Check the prerequisites on THIS machine (.NET 8 SDK, Inno Setup compiler).
      2. Publish the bot and the GUI, framework-dependent, into installer\publish\.
      3. Download the three dependency installers into installer\ (skipped if already there).
      4. Download and unpack a Vosk model into installer\vosk-model\ (skipped if already there).
      5. Compile installer\Setup.iss with Inno Setup.

    Safe to re-run: every step skips itself when its result is already in place.

.EXAMPLE
    .\build-installer.ps1
    Builds with the small Vosk model - the quickest build, smallest Setup.exe.

.EXAMPLE
    .\build-installer.ps1 -VoskModelSize Standard -Version 1.1
    Bundles the recommended (much better, but ~1.8 GB) model and stamps the installer as 1.1.

.EXAMPLE
    .\build-installer.ps1 -DryRun
    Only checks the prerequisites and prints what would happen - nothing is built or downloaded.
#>
[CmdletBinding()]
param(
    # Repository root. Defaults to the folder this script sits in.
    [string]$ProjectRoot = $PSScriptRoot,

    # Which Vosk model gets bundled into the installer.
    #   Small    ~40 MB   - fine for a first test, noticeably error-prone
    #   Standard ~1.8 GB  - recommended for real use, makes a ~1.8 GB Setup.exe
    #   Large    ~2.3 GB  - best accuracy, biggest installer
    [ValidateSet("Small", "Standard", "Large")]
    [string]$VoskModelSize = "Small",

    # Version stamped into the installer and its file name. It is also what the NEXT installer
    # compares against to decide whether it is upgrading, reinstalling or downgrading, so give
    # every build you hand out its own number. Digits and dots only (1.0, 1.2.3, 2.0.0.0).
    [ValidatePattern('^\d+(\.\d+){0,3}$')]
    [string]$Version = "1.0",

    # Slim installer: no speech model bundled, which cuts the Setup.exe down to a few MB. The
    # target machine then needs a model of its own and VoskModelPath set by hand.
    [Alias("Slim")]
    [switch]$SkipVoskModel,

    # Don't download the dependency installers. Anything missing is simply not bundled, and that
    # dependency won't be installed on the target machine.
    [switch]$SkipDependencyDownload,

    # Delete previous publish output and installers before building.
    [switch]$Clean,

    # Run the checks and print the plan without publishing, downloading or compiling anything.
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# Everything shipped is built in Release. Visual Studio's default is Debug, and a Debug build
# carries debug assertions, no JIT optimizations and a DebuggableAttribute into the installer -
# this is hardcoded rather than exposed as a parameter so an installer can't accidentally be
# built from Debug output.
$Configuration = "Release"

# ---------------------------------------------------------------------------------------------
# Output helpers (same style as setup-dev-environment.ps1)
# ---------------------------------------------------------------------------------------------

function Write-Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Write-Ok($message)   { Write-Host "    [OK] $message"   -ForegroundColor Green }
function Write-Warn2($message){ Write-Host "    [!]  $message"   -ForegroundColor Yellow }
function Write-Fail($message) { Write-Host "    [FAIL] $message" -ForegroundColor Red }
function Write-Info($message) { Write-Host "    $message"        -ForegroundColor Gray }

$summary = New-Object System.Collections.Generic.List[string]

# Official download locations. All are permanent "latest" links maintained by the vendors.
$dependencyDownloads = [ordered]@{
    "WindowsDesktopRuntime8-x64.exe"  = @{
        Url   = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
        Needs = ".NET 8 Desktop Runtime - needed by the bot AND the GUI"
    }
    "VC_redist.x64.exe"               = @{
        Url   = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
        Needs = "Visual C++ Redistributable - needed by Vosk's native libvosk.dll"
    }
    "MicrosoftEdgeWebview2Setup.exe"  = @{
        Url   = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
        Needs = "WebView2 Runtime - needed by the GUI"
    }
}

$voskModelUrls = @{
    "Small"    = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip"
    "Standard" = "https://alphacephei.com/vosk/models/vosk-model-en-us-0.22.zip"
    "Large"    = "https://alphacephei.com/vosk/models/vosk-model-en-us-0.42-gigaspeech.zip"
}

# ---------------------------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) { $ProjectRoot = (Get-Location).Path }
$ProjectRoot   = (Resolve-Path -LiteralPath $ProjectRoot).Path

$botProject    = Join-Path $ProjectRoot "Darkstar.csproj"
$guiProject    = Join-Path $ProjectRoot "Darkstar.Gui\Darkstar.Gui.csproj"
$installerDir  = Join-Path $ProjectRoot "installer"
$issFile       = Join-Path $installerDir "Setup.iss"
$publishBot    = Join-Path $installerDir "publish\bot"
$publishGui    = Join-Path $installerDir "publish\gui"
$voskModelDir  = Join-Path $installerDir "vosk-model"
$outputDir     = Join-Path $installerDir "output"
# The .iss appends "-slim" to the file name when built without a model, so the two variants can
# sit next to each other without overwriting one another.
$setupSuffix   = if ($SkipVoskModel) { "-slim" } else { "" }
$expectedSetup = Join-Path $outputDir "DARKSTAR-Setup-$Version$setupSuffix.exe"

Write-Host ""
Write-Host "D.A.R.K.S.T.A.R. installer build" -ForegroundColor White
Write-Host "--------------------------------" -ForegroundColor White
Write-Info "Source:        $ProjectRoot"
Write-Info "Version:       $Version"
Write-Info "Configuration: $Configuration (Debug is never used for an installer)"
Write-Info "Vosk model:    $(if ($SkipVoskModel) { 'not bundled' } else { $VoskModelSize })"
if ($DryRun) { Write-Warn2 "DRY RUN - nothing will be built, downloaded or compiled." }

# ---------------------------------------------------------------------------------------------
# 1. Project structure
# ---------------------------------------------------------------------------------------------

Write-Step "Project structure"

$requiredPaths = [ordered]@{
    "Darkstar.csproj (bot, must sit directly in the root)" = $botProject
    "Darkstar.Gui\Darkstar.Gui.csproj"                     = $guiProject
    "installer\Setup.iss"                                  = $issFile
}

$structureOk = $true
foreach ($entry in $requiredPaths.GetEnumerator()) {
    if (Test-Path -LiteralPath $entry.Value) {
        Write-Ok $entry.Key
    }
    else {
        Write-Fail "$($entry.Key) not found at $($entry.Value)"
        $structureOk = $false
    }
}

if (-not $structureOk) {
    Write-Host ""
    Write-Fail "The source tree doesn't look complete - pass -ProjectRoot pointing at the repository root."
    exit 1
}

# ---------------------------------------------------------------------------------------------
# 2. .NET SDK
# ---------------------------------------------------------------------------------------------

Write-Step ".NET 8 SDK"

$dotnetOk = $false
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetCmd) {
    Write-Fail "'dotnet' not found. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
    $summary.Add("[FAIL] .NET 8 SDK missing")
}
else {
    $sdks = & dotnet --list-sdks 2>$null
    $sdk8 = $sdks | Where-Object { $_ -match "^8\." }
    if ($sdk8) {
        Write-Ok "found: $((($sdk8 | Select-Object -First 1) -split ' ')[0])"
        $dotnetOk = $true
    }
    else {
        Write-Fail "no 8.x SDK installed (found: $($sdks -join '; ')). Install the .NET 8 SDK."
        $summary.Add("[FAIL] .NET 8 SDK missing")
    }
}

# ---------------------------------------------------------------------------------------------
# 3. Inno Setup compiler
# ---------------------------------------------------------------------------------------------

Write-Step "Inno Setup compiler (ISCC.exe)"

function Find-InnoSetupCompiler {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 5\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    $onPath = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    return $null
}

$iscc = Find-InnoSetupCompiler

if ($null -eq $iscc -and -not $DryRun) {
    Write-Warn2 "Inno Setup not found."

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Info "Installing it via winget (JRSoftware.InnoSetup)..."
        & winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
        $iscc = Find-InnoSetupCompiler
    }
}

if ($null -ne $iscc) {
    Write-Ok "found: $iscc"
}
else {
    Write-Fail "Inno Setup 6 is required to compile the installer: https://jrsoftware.org/isdl.php"
    $summary.Add("[FAIL] Inno Setup missing")
}

if (-not $dotnetOk -or ($null -eq $iscc -and -not $DryRun)) {
    Write-Host ""
    Write-Fail "Prerequisites missing - see above. Nothing was built."
    exit 1
}

# ---------------------------------------------------------------------------------------------
# 4. Clean
# ---------------------------------------------------------------------------------------------

if ($Clean) {
    Write-Step "Cleaning previous build output"
    foreach ($dir in @($publishBot, $publishGui, $outputDir)) {
        if (Test-Path -LiteralPath $dir) {
            if (-not $DryRun) { Remove-Item -LiteralPath $dir -Recurse -Force }
            Write-Ok "removed $dir"
        }
    }

    # Also the per-project intermediate and build folders. Publishing wipes its own target, so
    # these can't leak into the installer - but leaving them means "-Clean" doesn't actually
    # rebuild from scratch, and a stale obj\ is exactly what hides a restore or asset problem
    # that would otherwise show up now rather than on somebody else's machine.
    # Every project in the solution, found rather than listed - a hard-coded list is what made the
    # bot's own compile exclusion go stale when Darkstar.Tests was added.
    $projectDirs = @($ProjectRoot) + (Get-ChildItem -LiteralPath $ProjectRoot -Directory |
        Where-Object { Get-ChildItem -LiteralPath $_.FullName -Filter *.csproj -File } |
        ForEach-Object { $_.FullName })

    foreach ($projectDir in $projectDirs) {
        foreach ($name in @("obj", "bin")) {
            $dir = Join-Path $projectDir $name
            if (Test-Path -LiteralPath $dir) {
                if (-not $DryRun) { Remove-Item -LiteralPath $dir -Recurse -Force }
                Write-Ok "removed $dir"
            }
        }
    }
}

# ---------------------------------------------------------------------------------------------
# 5. Publish bot and GUI
# ---------------------------------------------------------------------------------------------

Write-Step "Publishing bot and GUI ($Configuration, framework-dependent, win-x64)"

function Invoke-Publish($projectPath, $outputPath, $label, $expectedExe) {
    Write-Info "$label -> $outputPath ($Configuration)"

    if ($DryRun) {
        Write-Ok "$label would be published in $Configuration"
        return $true
    }

    # Wipe the target first: publishing doesn't remove files that are no longer produced, so
    # leftovers from an earlier build (or from someone copying Debug output in here) would
    # otherwise be picked up by the installer.
    if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Recurse -Force }

    & dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained false -o $outputPath --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "$label failed to publish (dotnet exit code $LASTEXITCODE)."
        return $false
    }

    $exePath = Join-Path $outputPath $expectedExe
    if (-not (Test-Path -LiteralPath $exePath)) {
        Write-Fail "$label published, but $expectedExe is missing in $outputPath."
        return $false
    }

    Write-Ok "$label published in $Configuration ($expectedExe)"
    return $true
}

$botOk = Invoke-Publish $botProject $publishBot "Bot" "Darkstar.exe"
$guiOk = Invoke-Publish $guiProject $publishGui "GUI" "Darkstar.ConfigEditor.exe"

if (-not $botOk -or -not $guiOk) {
    Write-Host ""
    Write-Fail "Publishing failed - see the dotnet output above. No installer was built."
    exit 1
}

$summary.Add("$(if ($DryRun) { "[OK] Bot and GUI would be published ($Configuration)" } else { "[OK] Bot and GUI published ($Configuration)" })")

# ---------------------------------------------------------------------------------------------
# 5b. What actually goes into the installer
# ---------------------------------------------------------------------------------------------

# Setup.iss packs publish\bot\* and publish\gui\* wholesale, so whatever a NuGet package decides
# to drop in there gets installed on somebody's machine. The two publish folders are therefore
# inventoried rather than trusted: this prints what is going in, and says so when something turns
# up that a running bot has no use for. It never fails the build - a new file type might be
# legitimate, and only a human can tell.
#
# The project files already exclude the two known offenders (localized resource DLLs and the XML
# API documentation of referenced assemblies) via SatelliteResourceLanguages and
# AllowedReferenceRelatedFileExtensions. This check is what notices when a package finds a third
# way, or when one of those properties gets lost in a merge.

function Show-PublishInventory($path, $label) {
    if (-not (Test-Path -LiteralPath $path)) { return }

    $files = Get-ChildItem -LiteralPath $path -Recurse -File
    if ($files.Count -eq 0) { return }

    $totalMb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
    Write-Info "$label`: $($files.Count) file(s), $totalMb MB"

    # Everything a framework-dependent app legitimately needs at runtime: its own assemblies and
    # their symbols, the dependency/runtime manifests, and the GUI's web assets under wwwroot.
    $expected = @(".exe", ".dll", ".pdb", ".json", ".config",
                  ".html", ".htm", ".css", ".js", ".mjs", ".map",
                  ".woff", ".woff2", ".ttf", ".eot", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".ico",
                  ".wasm", ".dat", ".br", ".gz", ".txt")

    $unexpected = $files | Where-Object { $expected -notcontains $_.Extension.ToLowerInvariant() }
    if ($unexpected) {
        Write-Warn2 "$label contains $($unexpected.Count) file(s) that a running bot has no use for:"
        foreach ($file in ($unexpected | Sort-Object -Property Length -Descending | Select-Object -First 8)) {
            $relative = $file.FullName.Substring($path.Length).TrimStart('\', '/')
            Write-Info "  $relative ($([math]::Round($file.Length / 1KB, 1)) KB)"
        }
        if ($unexpected.Count -gt 8) { Write-Info "  ... and $($unexpected.Count - 8) more" }
        Write-Info "  These will be installed on the target machine. Exclude them in the .csproj if they are not needed."
    }

    # A surviving language folder means SatelliteResourceLanguages didn't take effect.
    $languageDirs = Get-ChildItem -LiteralPath $path -Directory |
        Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]{2,4})?$' -and $_.Name -ne 'en' }
    if ($languageDirs) {
        Write-Warn2 "$label still has localized resource folder(s): $(($languageDirs | ForEach-Object { $_.Name }) -join ', ')"
        Write-Info "  Expected none - check that SatelliteResourceLanguages is still set in the .csproj."
    }
}

if (-not $DryRun) {
    Write-Step "Checking what goes into the installer"
    Show-PublishInventory $publishBot "Bot"
    Show-PublishInventory $publishGui "GUI"
    Write-Ok "publish folders inventoried (warnings above, if any, are advisory)"
}

# ---------------------------------------------------------------------------------------------
# 6. Dependency installers
# ---------------------------------------------------------------------------------------------

Write-Step "Dependency installers (bundled into the Setup.exe)"

foreach ($entry in $dependencyDownloads.GetEnumerator()) {
    $fileName = $entry.Key
    $target   = Join-Path $installerDir $fileName

    if (Test-Path -LiteralPath $target) {
        $sizeMb = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
        Write-Ok "$fileName already present ($sizeMb MB)"
        continue
    }

    if ($SkipDependencyDownload) {
        Write-Warn2 "$fileName missing and -SkipDependencyDownload was given - $($entry.Value.Needs) will NOT be installed on the target machine."
        $summary.Add("[!] $fileName not bundled")
        continue
    }

    if ($DryRun) {
        Write-Info "$fileName would be downloaded ($($entry.Value.Needs))"
        continue
    }

    try {
        Write-Info "Downloading $fileName ..."
        Invoke-WebRequest -Uri $entry.Value.Url -OutFile $target -UseBasicParsing
        $sizeMb = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
        Write-Ok "$fileName downloaded ($sizeMb MB)"
    }
    catch {
        Write-Warn2 "Could not download $fileName ($($_.Exception.Message)) - that dependency will not be bundled."
        $summary.Add("[!] $fileName not bundled (download failed)")
    }
}

# ---------------------------------------------------------------------------------------------
# 7. Vosk model
# ---------------------------------------------------------------------------------------------

Write-Step "Vosk model (bundled into the Setup.exe)"

function Test-VoskModelFolder($path) {
    # A usable model always has these two subfolders; a half-extracted download does not.
    return (Test-Path -LiteralPath (Join-Path $path "am")) -and (Test-Path -LiteralPath (Join-Path $path "conf"))
}

<#
.SYNOPSIS
    Unpacks a downloaded Vosk model archive into the folder the installer expects.
.DESCRIPTION
    The archives contain one top-level folder (vosk-model-...), while installer\vosk-model must
    hold the model's own files (am\, conf\, ...) directly. This flattens that one level when it is
    present, tolerates archives that are already flat, and refuses anything that isn't a model.
    Throws on failure so the caller can carry on without a bundled model.
#>
function Expand-ToFourPartVersion {
    <#
        Turns "1", "1.2" or "1.2.3" into the four-part form Windows requires for a file's
        VersionInfo ("1.0.0.0", "1.2.0.0", "1.2.3.0"). A version that already has four parts is
        returned unchanged. Kept as its own function so it can be tested without running a build.
    #>
    param([Parameter(Mandatory)] [string]$Version)

    $parts = @($Version.Split('.'))
    while ($parts.Count -lt 4) { $parts += "0" }
    return ($parts[0..3] -join '.')
}

function Expand-VoskModel {
    param(
        [Parameter(Mandatory)] [string]$ZipPath,
        [Parameter(Mandatory)] [string]$TargetDir,
        [Parameter(Mandatory)] [string]$ExtractDir
    )

    if (Test-Path -LiteralPath $ExtractDir) { Remove-Item -LiteralPath $ExtractDir -Recurse -Force }
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $ExtractDir -Force

    $sourceDir = $ExtractDir
    if (-not (Test-VoskModelFolder $sourceDir)) {
        $inner = Get-ChildItem -LiteralPath $ExtractDir -Directory |
                 Where-Object { Test-VoskModelFolder $_.FullName } |
                 Select-Object -First 1
        if ($null -ne $inner) { $sourceDir = $inner.FullName }
    }

    if (-not (Test-VoskModelFolder $sourceDir)) {
        throw "the archive doesn't look like a Vosk model (no am\ and conf\ folders inside)"
    }

    if (Test-Path -LiteralPath $TargetDir) { Remove-Item -LiteralPath $TargetDir -Recurse -Force }
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceDir "*") -Destination $TargetDir -Recurse -Force
}

$modelBundled = $false

if ($SkipVoskModel) {
    Write-Warn2 "-SkipVoskModel: no model is bundled. VoskModelPath has to be set by hand after installing."
    $summary.Add("[!] no Vosk model bundled")
}
elseif ((Test-Path -LiteralPath $voskModelDir) -and (Test-VoskModelFolder $voskModelDir)) {
    Write-Ok "model already prepared in installer\vosk-model (delete that folder to bundle a different size)"
    $modelBundled = $true
}
elseif ($DryRun) {
    Write-Info "$VoskModelSize model would be downloaded and unpacked into installer\vosk-model"
    $modelBundled = $true
}
else {
    $url = $voskModelUrls[$VoskModelSize]
    $tempRoot = [System.IO.Path]::GetTempPath()
    $zipPath = Join-Path $tempRoot "darkstar-vosk-$VoskModelSize.zip"
    $extractDir = Join-Path $tempRoot "darkstar-vosk-extract"

    try {
        if ($VoskModelSize -eq "Small") {
            Write-Info "Downloading the small model (~40 MB)..."
        }
        else {
            Write-Info "Downloading the $VoskModelSize model - this is one to a few GB and will take a while."
        }

        Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
        Expand-VoskModel -ZipPath $zipPath -TargetDir $voskModelDir -ExtractDir $extractDir

        Write-Ok "$VoskModelSize model unpacked into installer\vosk-model"
        $modelBundled = $true
    }
    catch {
        Write-Warn2 "Could not prepare the Vosk model ($($_.Exception.Message)) - building without it."
        $summary.Add("[!] no Vosk model bundled (download failed)")
    }
    finally {
        if (Test-Path -LiteralPath $zipPath)    { Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $extractDir) { Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($modelBundled) {
    $summary.Add("$(if ($DryRun) { "[OK] Vosk model would be bundled ($VoskModelSize)" } else { "[OK] Vosk model bundled ($VoskModelSize)" })")
}

# ---------------------------------------------------------------------------------------------
# 8. Compile the installer
# ---------------------------------------------------------------------------------------------

Write-Step "Compiling the installer"

if ($DryRun) {
    Write-Info "ISCC would compile $issFile into $expectedSetup"
    if ($SkipVoskModel) { Write-Info "  (slim build: /DNoVoskModel - no model component in the installer)" }
    Write-Host ""
    Write-Host "Dry run finished - prerequisites checked, nothing built." -ForegroundColor White
    foreach ($line in $summary) { Write-Info $line }
    exit 0
}

# Windows' file-version fields insist on exactly four numbers, while -Version may be "1.2".
# Pad it out rather than making the caller type the padding.
$versionInfo = Expand-ToFourPartVersion $Version

$isccArgs = @("/DMyAppVersion=$Version", "/DMyVersionInfo=$versionInfo")
if ($SkipVoskModel) { $isccArgs += "/DNoVoskModel" }   # leaves the model component out entirely

& $iscc @isccArgs $issFile
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Fail "Inno Setup failed (exit code $LASTEXITCODE) - see its output above."
    exit 1
}

if (-not (Test-Path -LiteralPath $expectedSetup)) {
    # Older/modified .iss files may name the output differently - report whatever landed there.
    $produced = Get-ChildItem -LiteralPath $outputDir -Filter "*.exe" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $produced) {
        Write-Fail "Inno Setup reported success, but no .exe was found in $outputDir."
        exit 1
    }
    $expectedSetup = $produced.FullName
}

$setupSizeMb = [math]::Round((Get-Item -LiteralPath $expectedSetup).Length / 1MB, 1)
Write-Ok "installer built ($setupSizeMb MB)"
$summary.Add("[OK] $expectedSetup ($setupSizeMb MB)")

# ---------------------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------------------

Write-Host ""
Write-Host "Done." -ForegroundColor White
Write-Host "-----" -ForegroundColor White
foreach ($line in $summary) {
    $color = if ($line.StartsWith("[OK]")) { "Green" } elseif ($line.StartsWith("[!]")) { "Yellow" } else { "Red" }
    Write-Host "    $line" -ForegroundColor $color
}

Write-Host ""
Write-Host "    Copy this file to the target machine and run it:" -ForegroundColor White
Write-Host "      $expectedSetup" -ForegroundColor Cyan
Write-Host ""
Write-Info "On the target machine the installer installs whatever is missing (.NET 8 Desktop Runtime,"
Write-Info "Visual C++ Redistributable, WebView2 Runtime), puts the bot and GUI in place, writes the"
Write-Info "bundled model's path into a fresh config.json, and can register the Windows Service."
Write-Info "Still to be filled in after installing: GeminiApiKey, the SRS server and the radios."
if ($SkipVoskModel) {
    Write-Host ""
    Write-Warn2 "Slim build: no speech model included. Without VoskModelPath pointing at a model on the"
    Write-Warn2 "target machine, the bot falls back to a volume-based placeholder that recognizes no words."
}
