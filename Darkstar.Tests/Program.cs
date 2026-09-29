using Darkstar.Tests;

// Every suite, in one run: "dotnet run --project Darkstar.Tests".
//
// Exit code 0 means every assertion held, 1 means at least one did not - so this works unchanged as
// a step in CI (see .github/workflows/build.yml) and as something to run before opening a pull
// request. Nothing here connects to SRS, DCS or Gemini; the tests are about arithmetic, text and the
// arrangement of the code, and run on a machine with none of that installed.

Console.WriteLine("D.A.R.K.S.T.A.R. test suite");
Console.WriteLine($"Repository: {Test.Root}");

// A precondition, not an assertion inside a suite: a test that reads a file by an absolute path
// passes on the machine it was written on and throws everywhere else, halfway through the run, with a
// stack trace where an explanation belongs. Checked here so it is reported before anything runs.
//
// Not hypothetical - these suites were developed outside the repository and converted into it, and
// the conversion left twelve such paths behind. Every source read goes through
// Test.ReadSource("relative/path") for that reason.
if (Test.FindAbsolutePaths() is { Count: > 0 } strays)
{
    Console.WriteLine();
    Console.WriteLine($"ERROR: {strays.Count} test line(s) use an absolute path, which only works on");
    Console.WriteLine("       the machine they were written on. Use ReadSource(\"relative/path\").");
    foreach (var stray in strays.Take(10)) Console.WriteLine($"  {stray}");
    return 1;
}

var started = DateTime.Now;

Console.WriteLine();
Console.WriteLine("======== SRS paths, Vosk model checks, detector wiring ========");
SrsAndVoskTests.Run();

Console.WriteLine();
Console.WriteLine("======== Audio front end ========");
AudioFrontEndTests.Run();

Console.WriteLine();
Console.WriteLine("======== Pilot names ========");
PilotNameTests.Run();

Console.WriteLine();
Console.WriteLine("======== Airfields, ATIS, radio roles ========");
AirfieldTests.Run();

Console.WriteLine();
Console.WriteLine("======== Housekeeping, requests, documentation ========");
await HousekeepingTests.RunAsync();

Console.WriteLine();
Console.WriteLine(new string('=', 62));
Console.WriteLine($"{Test.Passed} passed, {Test.Failed} failed " +
                  $"in {(DateTime.Now - started).TotalSeconds:0.0}s");

return Test.Failed == 0 ? 0 : 1;
