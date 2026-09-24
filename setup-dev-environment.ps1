#Requires -Version 5.1
<#
.SYNOPSIS
    Sets up a Windows development environment for the D.A.R.K.S.T.A.R. project
    (bot service + Darkstar.Core + Blazor-Hybrid GUI).

.DESCRIPTION
    Checks/installs the prerequisites that repeatedly caused build problems during development:
      - .NET 8 SDK
      - Visual Studio 2022 workloads needed for WPF + Blazor (.NET desktop dev, ASP.NET/web dev)
      - WebView2 Runtime (needed by Darkstar.Gui)
      - Visual C++ Redistributable (needed by Vosk's native libvosk.dll - the Vosk NuGet
        package's own native binary is restored automatically by NuGet, but this system-level
        runtime dependency is not, and its absence otherwise only surfaces as a confusing
        DllNotFoundException/BadImageFormatException the first time hotword detection runs)
      - A Vosk speech model (needed by VoskModelPath in config.json), with a sanity check that
        the folder actually looks like a complete, unpacked model rather than a partial/failed
        extraction
      - NuGet restore + a trial build of the whole solution

    Safe to re-run: every step first checks whether it's already satisfied and skips itself if so.

.PARAMETER ProjectRoot
    Path to the solution root. Expected layout (matches the relative <ProjectReference> paths
    inside the .csproj files, so it must be exact):
        <ProjectRoot>\Darkstar.sln
        <ProjectRoot>\Darkstar.csproj        (bot project - lives directly in the root, NOT in
                                               its own "Darkstar\" subfolder)
        <ProjectRoot>\Program.cs, BotService.cs, ... (bot source files, also directly in the root)
        <ProjectRoot>\Darkstar.Core\Darkstar.Core.csproj
        <ProjectRoot>\Darkstar.Gui\Darkstar.Gui.csproj
    Defaults to K:\Darkstar\BOT_SRS.

.PARAMETER VoskModelSize
    Which pre-defined English Vosk model to install: "Small" (~40 MB, fast, but noticeably
    weaker recognition quality - this is what earlier versions of this script always used),
    "Standard" (~1.8 GB, the generally recommended accuracy/size tradeoff, and the one to try
    first if hotword recognition quality is a problem), or "Large" (~2.3 GB, gigaspeech-trained,
    best accuracy, most RAM/CPU while loaded). Defaults to "Small" to keep a fresh checkout's
    first setup fast; re-run with -VoskModelSize Standard (after deleting the old models\ folder,
    or the existing-model check below will just keep the small one) to upgrade. Ignored if
    -VoskModelUrl is also given.

.PARAMETER VoskModelUrl
    Direct download URL of the Vosk model to install - overrides -VoskModelSize entirely, for
    any model not covered by the three pre-defined sizes. Browse more models at
    https://alphacephei.com/vosk/models and pass their .zip URL here.

.PARAMETER SkipWebView2
    Skip the WebView2 Runtime check/install (e.g. if you don't need to run the GUI on this machine).

.PARAMETER SkipVoskModel
    Skip downloading a Vosk model (e.g. if you already have one elsewhere and will set
    VoskModelPath manually).

.PARAMETER SkipVCRedist
    Skip the Visual C++ Redistributable check/install (e.g. if you know it's already present, or
    you don't need Vosk-based hotword detection on this machine).

.EXAMPLE
    .\setup-dev-environment.ps1

.EXAMPLE
    .\setup-dev-environment.ps1 -ProjectRoot "D:\Dev\Darkstar" -SkipWebView2

.EXAMPLE
    # Recognition quality too weak with the small model? Delete models\ first, then:
    .\setup-dev-environment.ps1 -VoskModelSize Standard
#>

[CmdletBinding()]
param(
    [string]$ProjectRoot = "K:\Darkstar\BOT_SRS",

    [ValidateSet("Small", "Standard", "Large")]
    [string]$VoskModelSize = "Small",

    [string]$VoskModelUrl,

    [switch]$SkipWebView2,

    [switch]$SkipVoskModel,

    [switch]$SkipVCRedist
)

$ErrorActionPreference = "Stop"

function Write-Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Write-Ok($message) {
    Write-Host "    [OK] $message" -ForegroundColor Green
}

function Write-Warn2($message) {
    Write-Host "    [!]  $message" -ForegroundColor Yellow
}

function Write-Fail($message) {
    Write-Host "    [FAIL] $message" -ForegroundColor Red
}

$summary = New-Object System.Collections.Generic.List[string]

# Resolve the effective Vosk model URL: an explicit -VoskModelUrl always wins, otherwise map
# -VoskModelSize to its known download. Kept here (rather than as a param default) so an
# explicitly passed -VoskModelUrl can be distinguished from "not given" regardless of parameter
# order.
$voskModelSizeUrls = @{
    "Small"    = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip"     # ~40 MB, fast, weakest accuracy
    "Standard" = "https://alphacephei.com/vosk/models/vosk-model-en-us-0.22.zip"            # ~1.8 GB, recommended default for real use
    "Large"    = "https://alphacephei.com/vosk/models/vosk-model-en-us-0.42-gigaspeech.zip" # ~2.3 GB, best accuracy, heaviest
}
$effectiveVoskModelUrl = if ($VoskModelUrl) { $VoskModelUrl } else { $voskModelSizeUrls[$VoskModelSize] }

# ---------------------------------------------------------------------------
Write-Step ".NET 8 SDK"
# ---------------------------------------------------------------------------
$dotnetOk = $false
try {
    $sdks = dotnet --list-sdks 2>$null
    if ($sdks -match "^8\.") {
        Write-Ok "Found .NET 8 SDK: $((($sdks -split "`n") | Where-Object { $_ -match '^8\.' } | Select-Object -First 1))"
        $dotnetOk = $true
    } else {
        Write-Fail "dotnet is installed, but no 8.x SDK was found."
    }
} catch {
    Write-Fail "dotnet CLI not found on PATH."
}

if (-not $dotnetOk) {
    Write-Warn2 "Download the .NET 8 SDK (not just the runtime) from https://dotnet.microsoft.com/download/dotnet/8.0 and re-run this script."
    $summary.Add("[MISSING] .NET 8 SDK - install manually, then re-run this script.")
}

# ---------------------------------------------------------------------------
Write-Step "Visual Studio 2022 workloads (WPF + Blazor)"
# ---------------------------------------------------------------------------
# Required for Darkstar.Gui (Blazor-Hybrid over WPF): the "managed desktop" workload gives WPF,
# the "web" workload gives the ASP.NET/Blazor tooling.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsInfo = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -format json | ConvertFrom-Json
    if ($vsInfo) {
        $installPath = $vsInfo.installationPath
        Write-Ok "Visual Studio found at: $installPath"

        $hasDesktop = Test-Path (Join-Path $installPath "MSBuild\Microsoft\WindowsDesktop")
        $hasWeb = Test-Path (Join-Path $installPath "MSBuild\Microsoft\VisualStudio\v17.0\Web")

        if ($hasDesktop) { Write-Ok ".NET desktop development workload looks present (WPF)." }
        else { Write-Warn2 ".NET desktop development workload not detected - needed for Darkstar.Gui (WPF)." }

        if ($hasWeb) { Write-Ok "ASP.NET and web development workload looks present (Blazor)." }
        else { Write-Warn2 "ASP.NET and web development workload not detected - needed for Darkstar.Gui (Blazor)." }

        if (-not $hasDesktop -or -not $hasWeb) {
            Write-Warn2 "Open Visual Studio Installer -> Modify, and enable:"
            Write-Warn2 "  - '.NET desktop development'"
            Write-Warn2 "  - 'ASP.NET and web development'"
            $summary.Add("[ACTION NEEDED] Enable missing VS workloads via Visual Studio Installer -> Modify (see above).")
        }
    } else {
        Write-Fail "vswhere.exe found, but no Visual Studio installation was detected."
        $summary.Add("[MISSING] Visual Studio 2022 - install from https://visualstudio.microsoft.com/ with the '.NET desktop development' and 'ASP.NET and web development' workloads.")
    }
} else {
    Write-Warn2 "Visual Studio Installer not found - skipping workload check (this is fine if you use VS Code / dotnet CLI only)."
}

# ---------------------------------------------------------------------------
if (-not $SkipWebView2) {
    Write-Step "WebView2 Runtime (needed by Darkstar.Gui)"
    # Same detection approach as installer\Setup.iss - checks the registry key the Evergreen
    # Runtime is documented to register under.
    $webview2Guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
    $webview2Installed = $false
    foreach ($path in @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$webview2Guid",
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$webview2Guid",
        "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$webview2Guid"
    )) {
        if (Test-Path $path) { $webview2Installed = $true; break }
    }

    if ($webview2Installed) {
        Write-Ok "WebView2 Runtime is already installed."
    } else {
        Write-Warn2 "WebView2 Runtime not found - downloading the Evergreen Bootstrapper..."
        try {
            $bootstrapperPath = Join-Path $env:TEMP "MicrosoftEdgeWebview2Setup.exe"
            Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $bootstrapperPath -UseBasicParsing
            Write-Host "    Installing (may prompt for admin rights)..."
            Start-Process -FilePath $bootstrapperPath -ArgumentList "/silent", "/install" -Wait
            Write-Ok "WebView2 Runtime installed."
        } catch {
            Write-Fail "Could not download/install WebView2 Runtime automatically: $_"
            Write-Warn2 "Install manually from https://developer.microsoft.com/microsoft-edge/webview2/"
            $summary.Add("[MISSING] WebView2 Runtime - install manually if the GUI fails to start.")
        }
    }
} else {
    Write-Step "WebView2 Runtime"
    Write-Warn2 "Skipped (-SkipWebView2)."
}

# ---------------------------------------------------------------------------
if (-not $SkipVCRedist) {
    Write-Step "Visual C++ Redistributable (needed by Vosk's native libvosk.dll)"
    # Same detection approach as installer\Setup.iss: the VC++ 2015-2022 Redistributable
    # registers this key with an "Installed" DWORD of 1. NuGet restores Vosk's own native
    # libvosk.dll automatically, but libvosk.dll itself depends on this system-level runtime -
    # without it, hotword detection fails at first use with a DllNotFoundException or
    # BadImageFormatException that gives no hint the actual cause is a missing redistributable.
    $vcRedistInstalled = $false
    try {
        $vcRedistKey = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64" -ErrorAction Stop
        if ($vcRedistKey.Installed -eq 1) { $vcRedistInstalled = $true }
    } catch {
        $vcRedistInstalled = $false
    }

    if ($vcRedistInstalled) {
        Write-Ok "Visual C++ Redistributable (x64) is already installed."
    } else {
        Write-Warn2 "Visual C++ Redistributable not found - downloading and installing..."
        try {
            $vcRedistPath = Join-Path $env:TEMP "vc_redist.x64.exe"
            Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile $vcRedistPath -UseBasicParsing
            Write-Host "    Installing (may prompt for admin rights)..."
            Start-Process -FilePath $vcRedistPath -ArgumentList "/install", "/quiet", "/norestart" -Wait
            Write-Ok "Visual C++ Redistributable installed."
        } catch {
            Write-Fail "Could not download/install the Visual C++ Redistributable automatically: $_"
            Write-Warn2 "Install manually from https://aka.ms/vs/17/release/vc_redist.x64.exe"
            $summary.Add("[MISSING] Visual C++ Redistributable - install manually, otherwise Vosk hotword detection will fail at runtime.")
        }
    }
} else {
    Write-Step "Visual C++ Redistributable"
    Write-Warn2 "Skipped (-SkipVCRedist)."
}

# ---------------------------------------------------------------------------
if (-not $SkipVoskModel) {
    Write-Step "Vosk speech model"
    $modelsDir = Join-Path $ProjectRoot "models"

    # A plain "does the folder have any files in it" check previously used here would also count
    # a partial/interrupted download or extraction as success. A real Vosk model always has an
    # "am" (acoustic model) subfolder plus a top-level "conf" folder - checking for those is a
    # much better signal that a complete model actually landed here.
    $hasModelFiles = (Test-Path $modelsDir) -and ((Get-ChildItem $modelsDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0)
    $looksComplete = (Test-Path (Join-Path $modelsDir "am")) -and (Test-Path (Join-Path $modelsDir "conf"))

    if ($hasModelFiles -and $looksComplete) {
        Write-Ok "A complete-looking Vosk model already exists at $modelsDir - skipping download."
        Write-Warn2 "This does NOT check whether it matches -VoskModelSize $VoskModelSize - if you're trying to"
        Write-Warn2 "upgrade to a bigger/more accurate model, delete $modelsDir first and re-run this script."
        Write-Host "    Make sure VoskModelPath in config.json points here."
    } elseif ($hasModelFiles -and -not $looksComplete) {
        Write-Fail "A models folder exists at $modelsDir, but it doesn't look like a complete Vosk model (missing 'am\' and/or 'conf\' subfolders)."
        Write-Warn2 "This usually means a previous download/extraction was interrupted or unpacked one folder level too deep."
        Write-Warn2 "Delete $modelsDir and re-run this script, or fix the folder manually so that '$modelsDir\am\...' exists directly."
        $summary.Add("[BROKEN] Vosk model folder at $modelsDir looks incomplete (missing am\/conf\) - see warning above.")
    } else {
        $sizeLabel = if ($VoskModelUrl) { "custom URL" } else { $VoskModelSize }
        Write-Host "    Downloading Vosk model ($sizeLabel) from $effectiveVoskModelUrl ..."
        if ($VoskModelSize -eq "Small" -and -not $VoskModelUrl) {
            Write-Warn2 "Using the small model - noticeably weaker recognition than Standard/Large. If hotword"
            Write-Warn2 "detection quality is a problem, re-run with -VoskModelSize Standard (after deleting models\)."
            Write-Host "    (This is a few dozen MB and may take a moment.)"
        } else {
            Write-Host "    (This is one to a few GB and will take a while - grab a coffee.)"
        }
        try {
            New-Item -ItemType Directory -Path $modelsDir -Force | Out-Null
            $zipPath = Join-Path $env:TEMP "vosk-model.zip"
            Invoke-WebRequest -Uri $effectiveVoskModelUrl -OutFile $zipPath -UseBasicParsing

            $extractTemp = Join-Path $env:TEMP "vosk-model-extract"
            if (Test-Path $extractTemp) { Remove-Item $extractTemp -Recurse -Force }
            Expand-Archive -Path $zipPath -DestinationPath $extractTemp -Force

            # The zip contains one top-level folder (e.g. vosk-model-small-en-us-0.15\) -
            # move its *contents* directly into models\, matching what VoskModelPath expects.
            $innerFolder = Get-ChildItem $extractTemp -Directory | Select-Object -First 1
            if ($innerFolder) {
                Get-ChildItem $innerFolder.FullName | Move-Item -Destination $modelsDir -Force
            } else {
                Get-ChildItem $extractTemp | Move-Item -Destination $modelsDir -Force
            }

            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
            Remove-Item $extractTemp -Recurse -Force -ErrorAction SilentlyContinue

            # Verify the result actually looks like a complete model rather than reporting
            # success just because the download/unpack commands didn't throw - a truncated
            # download or an unexpected zip layout would otherwise go unnoticed until the bot
            # fails to load the model at runtime.
            $downloadedLooksComplete = (Test-Path (Join-Path $modelsDir "am")) -and (Test-Path (Join-Path $modelsDir "conf"))
            if ($downloadedLooksComplete) {
                Write-Ok "Vosk model installed to $modelsDir"
                Write-Host "    Set VoskModelPath in config.json to: $modelsDir"
            } else {
                Write-Fail "Vosk model was downloaded and unpacked, but $modelsDir is missing 'am\' and/or 'conf\' - the zip layout may differ from what this script expects."
                Write-Warn2 "Check $modelsDir manually, or unpack the model yourself so that '$modelsDir\am\...' exists directly."
                $summary.Add("[BROKEN] Vosk model at $modelsDir doesn't look complete after download - check manually.")
            }
        } catch {
            Write-Fail "Could not download/unpack the Vosk model automatically: $_"
            Write-Warn2 "Download manually from https://alphacephei.com/vosk/models and unpack into $modelsDir"
            $summary.Add("[MISSING] Vosk model - download manually (see above) and set VoskModelPath in config.json.")
        }
    }
} else {
    Write-Step "Vosk speech model"
    Write-Warn2 "Skipped (-SkipVoskModel)."
}

# ---------------------------------------------------------------------------
Write-Step "Solution files"
# ---------------------------------------------------------------------------
$slnPath = Join-Path $ProjectRoot "Darkstar.sln"
$slnxPath = Join-Path $ProjectRoot "Darkstar.slnx"
$solutionFile = $null
if (Test-Path $slnPath) { $solutionFile = $slnPath }
elseif (Test-Path $slnxPath) { $solutionFile = $slnxPath }

if ($solutionFile) {
    Write-Ok "Found solution: $solutionFile"
} else {
    Write-Fail "No Darkstar.sln / Darkstar.slnx found under $ProjectRoot"
    Write-Warn2 "Check -ProjectRoot points at the right folder (containing Darkstar.csproj, Darkstar.Core\, Darkstar.Gui\)."
    $summary.Add("[BLOCKED] Solution file not found under $ProjectRoot - skipped restore/build.")
}

# ---------------------------------------------------------------------------
Write-Step "Project structure"
# ---------------------------------------------------------------------------
# Catches the #1 recurring problem during setup: a .csproj moved into (or extracted into) the
# wrong subfolder, which breaks the relative <ProjectReference> paths and produces a cryptic
# "Load failed" / "Unable to find project ..." error only once Visual Studio opens the solution.
# Checking the exact expected paths here gives a clear error immediately instead.
$expectedFiles = [ordered]@{
    "Darkstar.csproj (bot project, must be directly in the root)"        = Join-Path $ProjectRoot "Darkstar.csproj"
    "Darkstar.Core\Darkstar.Core.csproj"                                 = Join-Path $ProjectRoot "Darkstar.Core\Darkstar.Core.csproj"
    "Darkstar.Gui\Darkstar.Gui.csproj"                                   = Join-Path $ProjectRoot "Darkstar.Gui\Darkstar.Gui.csproj"
}

$structureOk = $true
foreach ($label in $expectedFiles.Keys) {
    $path = $expectedFiles[$label]
    if (Test-Path $path) {
        Write-Ok "$label"
    } else {
        Write-Fail "$label - not found at $path"
        $structureOk = $false
    }
}

if ($structureOk) {
    Write-Ok "Project layout matches what the .csproj ProjectReference paths expect."
} else {
    Write-Warn2 "One or more project files are missing at the expected relative path."
    Write-Warn2 "Most common cause: Darkstar.csproj was placed in its own subfolder (e.g. 'Darkstar\Darkstar.csproj')"
    Write-Warn2 "instead of directly in the solution root - move it (and its .cs files) up one level."
    $summary.Add("[BLOCKED] Project structure under $ProjectRoot doesn't match expected layout (see above) - restore/build skipped.")
}

# ---------------------------------------------------------------------------
if ($solutionFile -and $dotnetOk -and $structureOk) {
    Write-Step "NuGet restore"
    try {
        dotnet restore $solutionFile
        Write-Ok "Restore completed."
    } catch {
        Write-Fail "Restore failed: $_"
        $summary.Add("[FAILED] dotnet restore - see output above.")
    }

    Write-Step "Trial build (Debug)"
    try {
        dotnet build $solutionFile -c Debug --no-restore
        Write-Ok "Build completed."
    } catch {
        Write-Fail "Build failed: $_"
        $summary.Add("[FAILED] dotnet build - see output above.")
    }
}

# ---------------------------------------------------------------------------
Write-Step "Summary"
# ---------------------------------------------------------------------------
if ($summary.Count -eq 0) {
    Write-Host "    Environment looks fully set up." -ForegroundColor Green
} else {
    Write-Host "    Still needs attention:" -ForegroundColor Yellow
    foreach ($item in $summary) { Write-Host "    - $item" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "Reminder - this script does NOT set up (needs to stay manual, machine/account-specific):" -ForegroundColor DarkGray
Write-Host "  - GeminiApiKey in config.json (https://aistudio.google.com/apikey)" -ForegroundColor DarkGray
Write-Host "  - DCS-SimpleRadio-Standalone server install + ExternalAudioExePath" -ForegroundColor DarkGray
Write-Host "  - DCS-gRPC install/config on the DCS mission server" -ForegroundColor DarkGray
Write-Host "  - DiscordWebhookUrl, if you want status notifications" -ForegroundColor DarkGray
