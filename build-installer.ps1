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

    # Keep the debug symbols (.pdb) and XML documentation in the installed files. Left out by
    # default: they are several MB that nothing needs to RUN the bot, and a .pdb also carries the
    # absolute path of the machine it was built on. Pass this when a stack trace with line numbers
    # is worth more than those two things - a bug you are chasing on the target machine.
    [switch]$WithSymbols,

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

# The stamp that goes into every assembly this build produces, and that is read back out of the
# published one afterwards. Two jobs in one string:
#
#   1. It tells anyone looking at a running bot or at the config editor's title bar WHICH build
#      they have. Nothing used to say: every assembly reported 1.0.0.0.
#   2. It is different on every run, which is what forces MSBuild to recompile. Without that, a
#      source file whose timestamp is older than the previous output is silently skipped - the
#      compile is considered up to date and the installer ends up packing the PREVIOUS build.
#      That is not a theory: it reproduces in four lines (edit a file, set its date to last week,
#      publish - the old code is still in the output, with no warning anywhere).
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

$buildStamp    = (Get-Date).ToUniversalTime().ToString("yyyyMMddHHmmss")
$informational = "$Version+build.$buildStamp"
# Windows' file-version fields insist on exactly four numbers, while -Version may be "1.2".
$versionInfo   = Expand-ToFourPartVersion $Version

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
    # The registry first: this is where Inno Setup records where it put itself, so it finds an
    # installation wherever it lives - including a per-user one (winget installs that way when it
    # cannot elevate), which the fixed paths below miss entirely. Both registry views are checked
    # because the compiler is a 32-bit application on a 64-bit Windows.
    $registryKeys = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
    )

    foreach ($key in $registryKeys) {
        $location = (Get-ItemProperty -Path $key -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
        if (-not [string]::IsNullOrWhiteSpace($location)) {
            $exe = Join-Path $location "ISCC.exe"
            if (Test-Path -LiteralPath $exe) { return $exe }
        }
    }

    # Then the usual folders. Wildcarded by version so a future Inno Setup 7 is found too, and
    # sorted descending so the newest installed version wins.
    $roots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, "$env:LOCALAPPDATA\Programs") |
             Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($root in $roots) {
        $found = Get-ChildItem -LiteralPath $root -Directory -Filter "Inno Setup*" -ErrorAction SilentlyContinue |
                 Sort-Object Name -Descending |
                 ForEach-Object { Join-Path $_.FullName "ISCC.exe" } |
                 Where-Object { Test-Path -LiteralPath $_ } |
                 Select-Object -First 1
        if ($found) { return $found }
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

<#
.SYNOPSIS
    The informational version stamped into a published executable, '' when there is none.
.DESCRIPTION
    ProductVersion is where .NET puts AssemblyInformationalVersion, which is the field carrying
    the build stamp. Read from the file rather than from the build log: the log says what the
    compiler was asked to do, the file says what somebody is actually going to install.
#>
function Get-PublishedVersion($exePath) {
    try {
        return [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion
    }
    catch {
        return ""
    }
}

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

    # And the intermediates of the project being published, together with those of everything it
    # references. This is the fix for an installer that contains an older version of the code than
    # the source tree it was built from: MSBuild decides whether to recompile by comparing
    # timestamps, so a source file dated earlier than the last build's output is treated as
    # already built - which is what happens to every file in an archive unpacked over a working
    # copy. Nothing warns; the publish reports success and copies the previous assemblies.
    #
    # Costs a full compile per installer build. An installer is a release artefact: it has to
    # contain the tree it was built from, and ten seconds is not a reason to gamble on that.
    foreach ($intermediate in Get-ChildItem -LiteralPath $ProjectRoot -Directory -Recurse -Include obj, bin -ErrorAction SilentlyContinue) {
        Remove-Item -LiteralPath $intermediate.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Out-Host rather than letting it run bare: the output of an external command goes into the
    # FUNCTION'S return value, so every line dotnet printed used to come back alongside the
    # $true/$false - and "if (-not $botOk)" on a non-empty array is never true. The failure
    # guards below this line could not fire at all. Out-Host writes the same text to the console
    # and keeps the return value a single boolean.
    & dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained false -o $outputPath --nologo `
        -p:Version=$versionInfo -p:FileVersion=$versionInfo -p:InformationalVersion=$informational | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "$label failed to publish (dotnet exit code $LASTEXITCODE)."
        return $false
    }

    $exePath = Join-Path $outputPath $expectedExe
    if (-not (Test-Path -LiteralPath $exePath)) {
        Write-Fail "$label published, but $expectedExe is missing in $outputPath."
        return $false
    }

    # The point of the whole exercise: prove that what is about to be packed is the build that was
    # just made, rather than taking the compiler's word for it. The stamp is unique per run, so an
    # assembly carrying anything else was not produced by this build - which is exactly the failure
    # that put an older version of the bot into the installer with nothing to show for it.
    # Read from the managed assembly rather than from the .exe next to it. The .exe is the
    # apphost - a native launcher the SDK stamps separately - and verifying the thing that
    # actually contains the code is one less assumption. The .exe is the fallback for the case
    # where there is no .dll beside it.
    $assemblyPath = [System.IO.Path]::ChangeExtension($exePath, ".dll")
    if (-not (Test-Path -LiteralPath $assemblyPath)) { $assemblyPath = $exePath }

    $stamped = Get-PublishedVersion $assemblyPath
    if ($stamped -eq $informational) {
        Write-Ok "$label published in $Configuration ($expectedExe, $stamped)"
    }
    elseif ([string]::IsNullOrWhiteSpace($stamped)) {
        Write-Warn2 "$label published, but $expectedExe carries no version at all - cannot confirm it is this build."
        $summary.Add("[!] $label could not be verified against this build")
    }
    else {
        Write-Host ""
        Write-Fail "$label is NOT the build that was just made."
        Write-Info "  expected: $informational"
        Write-Info "  packed:   $stamped"
        Write-Info "That means the compile was skipped and older output was published. Re-run with"
        Write-Info "-Clean; if it happens again, something outside this script is writing into"
        Write-Info "$outputPath."
        return $false
    }

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

<#
.SYNOPSIS
    Checks that everything Setup.iss packs with a wildcard actually exists.
.DESCRIPTION
    Inno Setup does not warn about a Source: line that matches nothing - it stops with an error.
    So a Vosk download that failed four steps earlier (a warning that scrolls past) used to end
    the build with "No files found matching ...\vosk-model\*" and no installer, which reads like
    a broken script rather than a missing download.

    Returns the number of files found, 0 for "would make the compiler stop".
#>
function Measure-PayloadFolder($path) {
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    return @(Get-ChildItem -LiteralPath $path -File -Recurse -ErrorAction SilentlyContinue).Count
}

# Starts as what was asked for and may be forced on below - building a slim installer is always
# better than building none, as long as it says so clearly.
$buildSlim = [bool]$SkipVoskModel

if (-not $DryRun) {
    foreach ($payload in @(
        @{ Path = $publishBot; What = "the bot (installer\publish\bot)" },
        @{ Path = $publishGui; What = "the GUI (installer\publish\gui)" })) {

        if ((Measure-PayloadFolder $payload.Path) -eq 0) {
            Write-Host ""
            Write-Fail "Nothing to pack for $($payload.What) - the publish step produced no files."
            Write-Info "Re-run with -Clean, and check the dotnet output above for a restore or build error."
            exit 1
        }
    }

    if (-not $buildSlim -and (Measure-PayloadFolder $voskModelDir) -eq 0) {
        Write-Warn2 "installer\vosk-model holds no files, so there is no model to bundle."
        Write-Warn2 "Building the SLIM installer instead - it works, but VoskModelPath has to be set by hand"
        Write-Warn2 "on the target machine. Re-run once the model download works to get the full one."
        $summary.Add("[!] built slim: no speech model was available")
        $buildSlim = $true
    }
    elseif (-not $buildSlim -and -not (Test-VoskModelFolder $voskModelDir)) {
        Write-Warn2 "installer\vosk-model has files but no am\ and conf\ folders - that is a half-extracted"
        Write-Warn2 "download, not a usable model. Building the SLIM installer; delete the folder and re-run"
        Write-Warn2 "to try the download again."
        $summary.Add("[!] built slim: the model folder is incomplete")
        $buildSlim = $true
    }
}

# The file name carries the variant, so recompute it when the variant was just forced to change.
if ($buildSlim -ne [bool]$SkipVoskModel) {
    $setupSuffix   = if ($buildSlim) { "-slim" } else { "" }
    $expectedSetup = Join-Path $outputDir "DARKSTAR-Setup-$Version$setupSuffix.exe"
}

if ($DryRun) {
    Write-Info "ISCC would compile $issFile into $expectedSetup"
    if ($buildSlim) { Write-Info "  (slim build: /DNoVoskModel - no model component in the installer)" }
    if (-not $WithSymbols) { Write-Info "  (debug symbols and XML docs are left out - pass -WithSymbols to keep them)" }
    Write-Host ""
    Write-Host "Dry run finished - prerequisites checked, nothing built." -ForegroundColor White
    foreach ($line in $summary) { Write-Info $line }
    exit 0
}

$isccArgs = @("/DMyAppVersion=$Version", "/DMyVersionInfo=$versionInfo")
if ($buildSlim)   { $isccArgs += "/DNoVoskModel" }   # leaves the model component out entirely
if ($WithSymbols) { $isccArgs += "/DWithSymbols" }   # keeps the .pdb files in the payload

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
