using DeviceControl.Api;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Cross-language quality-model contract tests without an external test framework.
// Run from repository root: dotnet run --project backend/DeviceControl.ContractTests
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var config = StationConfig.Load(Path.Combine(root,"backend/DeviceControl.Api/config/station.json"));
var payload = File.ReadAllText(Path.Combine(root,"tests/fixtures/quality_contract.json"));
using var doc = JsonDocument.Parse(payload);
var cases = doc.RootElement.GetProperty("cases").EnumerateArray().ToArray();
var errors = new List<string>();
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
foreach (var c in cases)
{
    var name = c.GetProperty("id").GetString()!;
    var definition = JsonSerializer.Deserialize<List<SequenceStep>>(
        c.GetProperty("definition").GetRawText(), jsonOptions)!;
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(definition))));
    var revision = new FlowRevision("contract-"+name,"Draft",null,"contract","2026-01-01T00:00:00Z",hash,definition,"");
    var report = WorkflowQualityGate.Evaluate(config,revision);
    if (!WorkflowQualityGate.Verify(report)) errors.Add(name+" report SHA-256 mismatch");
    var expectedPass = c.GetProperty("expectedPass").GetBoolean();
    if (report.Pass != expectedPass) errors.Add(name+" expected Pass="+expectedPass+", actual "+report.Pass);
    foreach (var expected in c.GetProperty("expectedCases").EnumerateArray())
    {
        var id=expected.GetProperty("id").GetString()!;
        var outcome=expected.GetProperty("outcome").GetString()!;
        var result=report.Results.FirstOrDefault(r=>r.Id==id);
        if (result is null || result.Actual != outcome)
            errors.Add(name+":"+id+" expected "+outcome+", actual "+result?.Actual);
    }
    var count=c.GetProperty("expectedWarnings").GetInt32();
    if (report.Warnings.Count!=count)errors.Add(name+" expected "+count+" warnings, actual "+report.Warnings.Count);
    Console.WriteLine((report.Pass==expectedPass ? "OK  " : "FAIL") + " " + name);
}
// V1.2 process-local FIFO resource coordinator contracts. Runs only with .NET 10 SDK.
try
{
    var scheduler = new SharedResourceCoordinator();
    using var aLease = await scheduler.AcquireAsync("A","camera-A",["CELL-SHARED"],1500,CancellationToken.None);
    var bWait = scheduler.AcquireAsync("B","camera-B",["CELL-SHARED"],1500,CancellationToken.None);
    var cWait = scheduler.AcquireAsync("C","camera-C",["CELL-SHARED"],1500,CancellationToken.None);
    if (scheduler.OwnerOf("CELL-SHARED") != "A") errors.Add("V12 owner should remain A while B/C queue");
    aLease.Dispose();
    using var bLease = await bWait;
    if (scheduler.OwnerOf("CELL-SHARED") != "B") errors.Add("V12 FIFO next owner B");
    bLease.Dispose();
    using var cLease = await cWait;
    if (scheduler.OwnerOf("CELL-SHARED") != "C") errors.Add("V12 FIFO next owner C");
    cLease.Dispose();
    if (scheduler.OwnerOf("CELL-SHARED") != null) errors.Add("V12 lease release leak");
    using var held = await scheduler.AcquireAsync("A","one",["R1","R2"],1500,CancellationToken.None);
    try
    {
        using var shouldTimeOut = await scheduler.AcquireAsync("B","two",["R2"],120,CancellationToken.None);
        errors.Add("V12 resource timeout was not enforced");
    }
    catch (SharedResourceTimeoutException) { /* expected */ }
    using var stop = new CancellationTokenSource();
    var pending = scheduler.AcquireAsync("B","canceled",["R1"],1500,stop.Token);
    stop.Cancel();
    try { using var unexpected = await pending; errors.Add("V12 cancellation did not withdraw the request"); }
    catch (OperationCanceledException) { /* expected */ }
    held.Dispose();
    using var recovered = await scheduler.AcquireAsync("C","after-cancel",["R1","R2"],1000,CancellationToken.None);
    Console.WriteLine("OK   V1.2 shared-resource FIFO / timeout / cancel / atomic keys");
}
catch (Exception ex) { errors.Add("V12 resource-contract exception: " + ex); }
await RecoveryRegressions.Run(root, errors);
if (errors.Count>0)
{
    foreach (var issue in errors)Console.Error.WriteLine(issue);
    return 1;
}
Console.WriteLine($"PASS: {cases.Length} browser/C# workflow-quality contract fixtures");
return 0;
