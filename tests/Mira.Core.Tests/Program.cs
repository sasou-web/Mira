// Mira's checks that need neither WPF nor Windows (PortableTests), on any system:
//   dotnet run --project tests/Mira.Core.Tests/Mira.Core.Tests.csproj -c Release -- [--filter <texte>] [--list]
// Mira.Tests runs the same checks on Windows, with those of the Windows app.
var run = new TestRun(args);
await PortableTests.RunAsync(run);
return run.Finish();
