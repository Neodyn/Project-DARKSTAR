using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>Finding SRS, checking the Vosk model, and the detector wiring.</summary>
internal static class SrsAndVoskTests
{
    public static void Run()
    {
        // Builds fake SRS install trees on disk and checks that SrsPaths finds the executable in each
        // of the layouts it claims to support - and, just as importantly, that it does NOT report a
        // hit when there is nothing there.

        var root = Path.Combine(Path.GetTempPath(), "srspaths-test-" + Guid.NewGuid().ToString("N"));


        string MakeTree(string label, params string[] relativeFiles)
        {
            var baseDir = Path.Combine(root, label);
            foreach (var rel in relativeFiles)
            {
                var full = Path.Combine(baseDir, rel.Replace('\\', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "fake");
            }
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        string? Find(params string[] roots) =>
            SrsPaths.FindExternalAudioExe(roots, File.Exists, Directory.Exists);

        const string Exe = "DCS-SR-ExternalAudio.exe";

        try
        {
            Console.WriteLine("SrsPaths - layouts under a Program Files style root");

            // 1. The real default layout the user reported.
            var t1 = MakeTree("default", @"DCS-SimpleRadio-Standalone\ExternalAudio\" + Exe);
            Check("default install (DCS-SimpleRadio-Standalone\\ExternalAudio)",
                Find(t1) == Path.Combine(t1, "DCS-SimpleRadio-Standalone", "ExternalAudio", Exe),
                Find(t1) ?? "<null>");

            // 2. Older builds put it straight in the install folder.
            var t2 = MakeTree("flat", @"DCS-SimpleRadio-Standalone\" + Exe);
            Check("flat install (exe directly in the SRS folder)",
                Find(t2) == Path.Combine(t2, "DCS-SimpleRadio-Standalone", Exe));

            // 3. The old path the config used to default to.
            var t3 = MakeTree("serverfolder", @"DCS-SimpleRadio-Standalone\Server\" + Exe);
            Check("Server subfolder (the old hard-coded default)",
                Find(t3) == Path.Combine(t3, "DCS-SimpleRadio-Standalone", "Server", Exe));

            // 4. Server package with its own ExternalAudio folder.
            var t4 = MakeTree("serverpkg", @"DCS-SimpleRadio-Standalone-Server\ExternalAudio\" + Exe);
            Check("server package folder name",
                Find(t4) == Path.Combine(t4, "DCS-SimpleRadio-Standalone-Server", "ExternalAudio", Exe));

            // 5. Client subfolder.
            var t5 = MakeTree("clientpkg", @"DCS-SimpleRadio\Client\ExternalAudio\" + Exe);
            Check("Client\\ExternalAudio layout",
                Find(t5) == Path.Combine(t5, "DCS-SimpleRadio", "Client", "ExternalAudio", Exe));

            // 6. Root IS the install folder (what the GUI does with a folder the user typed).
            var t6 = MakeTree("asroot", @"ExternalAudio\" + Exe);
            Check("search root is the SRS folder itself",
                Find(t6) == Path.Combine(t6, "ExternalAudio", Exe));

            Console.WriteLine();
            Console.WriteLine("SrsPaths - negative cases");

            var empty = MakeTree("empty");
            Check("empty folder finds nothing", Find(empty) is null, Find(empty) ?? "");

            var wrongName = MakeTree("wrongname", @"DCS-SimpleRadio-Standalone\ExternalAudio\SomethingElse.exe");
            Check("wrong executable name is not accepted", Find(wrongName) is null, Find(wrongName) ?? "");

            var wrongDepth = MakeTree("wrongdepth", @"DCS-SimpleRadio-Standalone\A\B\C\" + Exe);
            Check("unknown nesting is not blindly searched", Find(wrongDepth) is null, Find(wrongDepth) ?? "");

            Check("non-existent root is skipped, not thrown on",
                Find(Path.Combine(root, "does-not-exist")) is null);

            Check("empty/blank roots are ignored", Find("", "   ") is null);

            Console.WriteLine();
            Console.WriteLine("SrsPaths - probe order and root handling");

            // ExternalAudio must win over the flat exe when both are present, because that is where
            // current SRS versions keep the real one.
            var both = MakeTree("both",
                @"DCS-SimpleRadio-Standalone\ExternalAudio\" + Exe,
                @"DCS-SimpleRadio-Standalone\" + Exe);
            Check("ExternalAudio wins over a stray exe in the install root",
                Find(both) == Path.Combine(both, "DCS-SimpleRadio-Standalone", "ExternalAudio", Exe),
                Find(both) ?? "<null>");

            // Earlier roots win over later ones.
            var second = MakeTree("second", @"DCS-SimpleRadio-Standalone\ExternalAudio\" + Exe);
            Check("first matching root wins",
                Find(empty, second) == Path.Combine(second, "DCS-SimpleRadio-Standalone", "ExternalAudio", Exe));

            Check("a root listed twice yields no duplicate work (still finds it)",
                Find(t1, t1) == Path.Combine(t1, "DCS-SimpleRadio-Standalone", "ExternalAudio", Exe));

            var candidates = SrsPaths.EnumerateCandidates(new[] { t1, t1 }, Directory.Exists).ToList();
            Check("candidate list is de-duplicated",
                candidates.Count == candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                $"{candidates.Count} candidates");

            Check("every candidate ends in the executable name",
                candidates.All(c => c.EndsWith(Exe, StringComparison.OrdinalIgnoreCase)));

            // An exception from the existence check must not blow up the probe.
            Check("a throwing existence check is treated as 'not there'",
                SrsPaths.FindExternalAudioExe(new[] { t1 },
                    _ => throw new UnauthorizedAccessException(), Directory.Exists) is null);

            Console.WriteLine();
            Console.WriteLine("SrsPaths.ResolveToExecutable");

            var exePath = Path.Combine(t1, "DCS-SimpleRadio-Standalone", "ExternalAudio", Exe);

            Check("a correct exe path is returned unchanged",
                SrsPaths.ResolveToExecutable(exePath, File.Exists, Directory.Exists) == exePath);

            Check("the SRS install folder resolves to the exe",
                SrsPaths.ResolveToExecutable(Path.Combine(t1, "DCS-SimpleRadio-Standalone"), File.Exists, Directory.Exists) == exePath);

            Check("the ExternalAudio folder itself resolves to the exe",
                SrsPaths.ResolveToExecutable(Path.Combine(t1, "DCS-SimpleRadio-Standalone", "ExternalAudio"), File.Exists, Directory.Exists) == exePath);

            Check("surrounding quotes are tolerated",
                SrsPaths.ResolveToExecutable("\"" + exePath + "\"", File.Exists, Directory.Exists) == exePath);

            Check("whitespace is trimmed",
                SrsPaths.ResolveToExecutable("  " + exePath + "  ", File.Exists, Directory.Exists) == exePath);

            Check("null/empty input returns null",
                SrsPaths.ResolveToExecutable(null, File.Exists, Directory.Exists) is null &&
                SrsPaths.ResolveToExecutable("   ", File.Exists, Directory.Exists) is null);

            Check("a path that exists nowhere returns null (caller keeps the user's text)",
                SrsPaths.ResolveToExecutable(@"C:\nope\nothing.exe", File.Exists, Directory.Exists) is null);

            Console.WriteLine();
            Console.WriteLine("Consistency with the installer script and the config default");

            var iss = ReadSource("installer/Setup.iss");

            foreach (var segments in SrsPaths.ExeRelativePaths)
            {
                var windowsPath = "\\" + string.Join("\\", segments);
                Check($"Setup.iss probes '{windowsPath}'", iss.Contains("'" + windowsPath + "'"));
            }

            foreach (var folder in SrsPaths.InstallFolderNames)
                Check($"Setup.iss probes folder '{folder}'", iss.Contains("'\\" + folder + "'"));

            Check("Setup.iss seeds ExternalAudioExePath into config.json",
                iss.Contains("\"ExternalAudioExePath\""));

            Check("Setup.iss still seeds VoskModelPath",
                iss.Contains("\"VoskModelPath\""));

            Check("the config default points at the ExternalAudio folder",
                SrsPaths.DefaultExternalAudioExePath ==
                @"C:\Program Files\DCS-SimpleRadio-Standalone\ExternalAudio\DCS-SR-ExternalAudio.exe");

            var appConfig = ReadSource("Darkstar.Core/AppConfig.cs");
            Check("AppConfig no longer hard-codes the old Server path",
                !appConfig.Contains(@"DCS-SimpleRadio-Standalone\Server\DCS-SR-ExternalAudio.exe"));

            // ---------------------------------------------------------------------------------------
            // VoskModelCheck - the guard that keeps a bad model path from killing the process inside
            // the native library (AccessViolationException, which .NET cannot catch).
            // ---------------------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("VoskModelCheck - valid models");

            string MakeModel(string label, params string[] extraFiles)
            {
                var dir = Path.Combine(root, label);
                foreach (var rel in new[] { @"am\final.mdl", @"conf\model.conf", @"conf\mfcc.conf" }.Concat(extraFiles))
                {
                    var full = Path.Combine(dir, rel.Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, "x");
                }
                Directory.CreateDirectory(Path.Combine(dir, "graph"));
                Directory.CreateDirectory(Path.Combine(dir, "ivector"));
                return dir;
            }

            var model = MakeModel("vosk-model-small-en-us-0.15");
            var r = VoskModelCheck.Check(model);
            Check("a proper model folder is accepted",
                r.IsUsable && r.ResolvedPath == model && r.Message == "", r.Message);

            Check("LooksLikeModel agrees", VoskModelCheck.LooksLikeModel(model));

            // Only am\final.mdl present (some model layouts vary) - still recognisable.
            var amOnly = Path.Combine(root, "am-only");
            Directory.CreateDirectory(Path.Combine(amOnly, "am"));
            File.WriteAllText(Path.Combine(amOnly, "am", "final.mdl"), "x");
            Check("a folder with only am\\final.mdl is accepted", VoskModelCheck.Check(amOnly).IsUsable);

            Console.WriteLine();
            Console.WriteLine("VoskModelCheck - the case that crashed the bot");

            var missing = Path.Combine(root, "no-such-folder");
            r = VoskModelCheck.Check(missing);
            Check("a missing folder is reported, not passed to Vosk",
                r.Status == VoskModelStatus.FolderMissing && !r.IsUsable && r.ResolvedPath is null);
            Check("the message names the folder", r.Message.Contains(missing));
            Check("the message comes with something to do about it", r.Hints.Count > 0);

            var notAModel = Path.Combine(root, "empty-models-folder");
            Directory.CreateDirectory(notAModel);
            r = VoskModelCheck.Check(notAModel);
            Check("an existing folder that isn't a model is rejected",
                r.Status == VoskModelStatus.NotAModelFolder && !r.IsUsable);

            // Half-finished download: the folders are there, the files aren't.
            var partial = Path.Combine(root, "partial");
            Directory.CreateDirectory(Path.Combine(partial, "am"));
            Directory.CreateDirectory(Path.Combine(partial, "conf"));
            r = VoskModelCheck.Check(partial);
            Check("empty am\\ and conf\\ folders are not mistaken for a model",
                r.Status == VoskModelStatus.NotAModelFolder);

            r = VoskModelCheck.Check("");
            Check("an empty path is 'not configured', not an error",
                r.Status == VoskModelStatus.NotConfigured && !r.IsUsable);
            Check("a whitespace-only path is treated the same",
                VoskModelCheck.Check("   ").Status == VoskModelStatus.NotConfigured);

            Console.WriteLine();
            Console.WriteLine("VoskModelCheck - model unpacked one level too deep");

            var outer = Path.Combine(root, "models-wrapper");
            Directory.CreateDirectory(outer);
            var inner = MakeModel(Path.Combine("models-wrapper", "vosk-model-small-en-us-0.15"));
            r = VoskModelCheck.Check(outer);
            Check("the model inside is found", r.IsUsable && r.ResolvedPath == inner, r.ResolvedPath ?? "<null>");
            Check("and the discrepancy is mentioned rather than hidden",
                r.Message.Contains(outer) && r.Message.Contains(inner));
            Check("with a hint to fix the config", r.Hints.Count > 0);

            // Two levels deep is NOT searched - that would turn a typo into a slow directory walk.
            var tooDeep = Path.Combine(root, "too-deep");
            Directory.CreateDirectory(tooDeep);
            MakeModel(Path.Combine("too-deep", "a", "model"));
            Check("nested two levels deep is not silently used",
                VoskModelCheck.Check(tooDeep).Status == VoskModelStatus.NotAModelFolder);

            Check("quotes and whitespace around the path are tolerated",
                VoskModelCheck.Check("  \"" + model + "\"  ").IsUsable);

            Check("a throwing file check doesn't propagate",
                VoskModelCheck.Check(model, Directory.Exists,
                    _ => throw new UnauthorizedAccessException(),
                    _ => Array.Empty<string>()).Status == VoskModelStatus.NotAModelFolder);

            Console.WriteLine();
            Console.WriteLine("Startup behaviour");

            var botService = ReadSource("BotService.cs");
            Check("a bad model stops the bot instead of falling back to the volume detector",
                botService.Contains("catch (VoskModelLoadException ex)") && botService.Contains("FailStartup"));
            Check("ExecuteAsync no longer lets exceptions escape into the runtime",
                botService.Contains("await RunAsync(stoppingToken)") && botService.Contains("catch (Exception ex)"));

            var detector = ReadSource("VoskHotwordDetector.cs");
            Check("the model is checked before the native call",
                detector.IndexOf("VoskModelCheck.Check", StringComparison.Ordinal) <
                detector.IndexOf("new Model(", StringComparison.Ordinal));
            Check("the loaded model's native handle is verified before use",
                detector.Contains("HasNativeHandle"));
            Check("a missing libvosk.dll is reported as such",
                detector.Contains("DllNotFoundException") && detector.Contains("vc_redist"));

            var program = ReadSource("Program.cs");
            Check("unhandled exceptions are logged to file",
                program.Contains("UnhandledException") && program.Contains("UnobservedTaskException"));

            Console.WriteLine();
            Console.WriteLine("Installer - upgrading an existing installation");

            // The AppId is what identifies the installation to Windows; the [Code] section repeats it to
            // read the installed version back. If the two ever drift apart, every upgrade silently
            // becomes a first install - so check they are the same string.
            var appIdLine = System.Text.RegularExpressions.Regex.Match(iss, @"^AppId=\{\{(?<id>[^\r\n]+)$",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            Check("AppId is defined", appIdLine.Success);
            if (appIdLine.Success)
            {
                var appId = "{" + appIdLine.Groups["id"].Value.Trim();
                Check("the [Code] section reads the uninstall key of that same AppId",
                    iss.Contains("Uninstall\\" + appId + "_is1"), appId);
            }

            Check("the installed version is read back before installing",
                iss.Contains("function GetInstalledVersion") && iss.Contains("'DisplayVersion'"));
            Check("versions are compared, not just detected",
                iss.Contains("StrToVersion") && iss.Contains("ComparePackedVersion"));
            Check("a downgrade asks before overwriting a newer version",
                iss.Contains("which is NEWER than this installer"));
            Check("the service is stopped before any file is replaced (PrepareToInstall)",
                iss.Contains("function PrepareToInstall") && iss.Contains("StopDarkstarService"));
            Check("stopping waits for the service to actually reach Stopped",
                iss.Contains("WaitForStatus"));
            Check("the service is started again afterwards",
                iss.Contains("StartDarkstarService") && iss.Contains("ServiceWasStopped"));
            Check("an existing service registration is replaced rather than left stale",
                iss.Contains("\"delete \"\"{#MyServiceName}\"\"\"") && iss.Contains("Check: DarkstarServiceInstalled"));
            Check("the previous install folder and choices are reused",
                iss.Contains("UsePreviousAppDir=yes") && iss.Contains("UsePreviousTasks=yes"));
            Check("files held open by the config editor are handled",
                iss.Contains("CloseApplications=yes"));
            Check("the Setup.exe carries a four-part file version",
                iss.Contains("VersionInfoVersion={#MyVersionInfo}"));
            Check("the Ready page says whether this is an update or a reinstall",
                iss.Contains("function UpdateReadyMemo") && iss.Contains("Updating version "));

            var build = ReadSource("build-installer.ps1");
            Check("the build script passes the four-part version to Inno Setup",
                build.Contains("/DMyVersionInfo=$versionInfo"));
            Check("and rejects a version string Windows couldn't use",
                build.Contains(@"[ValidatePattern('^\d+(\.\d+){0,3}$')]"));

            Console.WriteLine();
            Console.WriteLine("Wake word audio path - wiring");

            var appCfg = ReadSource("Darkstar.Core/AppConfig.cs");
            Check("the low-pass filter is the default, not opt-in",
                appCfg.Contains("HotwordAudioFilter { get; set; } = HotwordAudioFilter.LowPass"));
            Check("automatic gain is opt-in",
                appCfg.Contains("HotwordAutoGain { get; set; } = false"));
            Check("saving recordings is opt-in",
                appCfg.Contains("SaveRecordings { get; set; } = false"));

            var det = ReadSource("VoskHotwordDetector.cs");
            Check("the detector no longer carries its own downsampler",
                !det.Contains("Downsample48kTo16k"));
            Check("the filter instance belongs to the detector, i.e. one per radio",
                det.Contains("private readonly DecimatingLowPass? _lowPass"));
            Check("automatic gain works on a copy, so recordings stay untouched",
                det.Contains("pcm16Mono48k.Clone()"));

            var bot = ReadSource("BotService.cs");
            // Checked as two separate tokens rather than one string: the constructor call spans several
            // lines now that it also passes the accepted wake-word variants, and a test that breaks on
            // line wrapping tells you nothing about the behaviour.
            var detectorCall = bot[bot.IndexOf("new VoskHotwordDetector(", StringComparison.Ordinal)..];
            detectorCall = detectorCall[..detectorCall.IndexOf(';')];
            Check("the config settings reach the detector",
                detectorCall.Contains("config.HotwordAudioFilter") && detectorCall.Contains("config.HotwordAutoGain"),
                detectorCall.Replace("\n", " "));
            Check("and so do the accepted wake word variants",
                detectorCall.Contains("acceptedPhrases"));
            Check("both hits and misses are saved when asked for",
                bot.Contains("SaveRecording(audio, \"hit\"") && bot.Contains("SaveRecording(audio, \"missed\""));
            Check("a failed recording write can't disturb the radio work",
                bot.Contains("[Recording] Could not save a recording"));

            var prog = ReadSource("Program.cs");
            Check("--test-hotword runs before the host starts (never touches SRS)",
                prog.IndexOf("HotwordTestRunner.IsRequested", StringComparison.Ordinal) <
                prog.IndexOf("Host.CreateApplicationBuilder", StringComparison.Ordinal));

            var runner = ReadSource("HotwordTestRunner.cs");
            Check("the runner feeds audio in the same 20 ms frames as the live path",
                runner.Contains("FrameSamples = 960"));
            Check("it can compare both audio paths on the same files",
                runner.Contains("--compare") && runner.Contains("RunComparison"));
            Check("expectations come from the file names the bot itself writes",
                runner.Contains("_hit_") && runner.Contains("_missed_"));

            var speech = ReadSource("Darkstar.Gui/Pages/SpeechPanel.razor");
            Check("all three settings are reachable from the GUI",
                speech.Contains("HotwordAudioFilter") && speech.Contains("HotwordAutoGain") && speech.Contains("SaveRecordings"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }
}
