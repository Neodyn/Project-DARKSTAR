using Darkstar.Tests;

// Every suite, in one run: "dotnet run --project Darkstar.Tests".
//
// Exit code 0 means every assertion held, 1 means at least one did not - so this works unchanged as
// a step in CI (see .github/workflows/build.yml) and as something to run before opening a pull
// request. Nothing here connects to SRS, DCS or Gemini; the tests are about arithmetic, text and the
// arrangement of the code, and run on a machine with none of that installed.

Console.WriteLine("D.A.R.K.S.T.A.R. test suite");
Console.WriteLine($"Repository: {Test.Root}");

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
