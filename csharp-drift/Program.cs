// Step 10 (C# port): annual re-run + drift test. Mirrors python/annual_drift_check.py
// exactly — same Green/Yellow/Red classification logic, same snapshot-per-day
// audit-trail approach. See that file's docstring for the full rationale.
//
// Reads:  ../output/goal_portfolio_mapping.csv
//         ../data/snapshots/*.csv
// Writes: ../data/snapshots/{today}.csv
//         ../output/drift_report.csv
//
// Usage: dotnet run
//        dotnet run -- --drift-threshold 0.03
//        dotnet run -- --as-of 2027-08-13

using System.Globalization;
using MfToolkit;

var projectDir = FindProjectRoot(AppContext.BaseDirectory);
var repoRoot = projectDir.Parent!.FullName;
var goalMappingPath = Path.Combine(repoRoot, "output", "goal_portfolio_mapping.csv");
var snapshotsDir = Path.Combine(repoRoot, "data", "snapshots");
var driftReportPath = Path.Combine(repoRoot, "output", "drift_report.csv");

double driftThreshold = 0.05;
DateTime asOf = DateTime.Now;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--drift-threshold": driftThreshold = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--as-of": asOf = DateTime.ParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
    }
}

if (!File.Exists(goalMappingPath))
    throw new FileNotFoundException($"{goalMappingPath} not found — run csharp-goals first.");

Directory.CreateDirectory(snapshotsDir);
var current = SimpleCsv.ReadWithHeader(goalMappingPath);
string todayStr = asOf.ToString("yyyy-MM-dd");

var priorCandidates = Directory.GetFiles(snapshotsDir, "*.csv")
    .Where(p => Path.GetFileNameWithoutExtension(p) != todayStr)
    .OrderByDescending(p => p)
    .ToList();

var snapshotPath = Path.Combine(snapshotsDir, $"{todayStr}.csv");

if (priorCandidates.Count == 0)
{
    Console.WriteLine("No prior snapshot found — this is the first run.");
    Console.WriteLine("Establishing this run as the baseline. No drift comparison is possible yet;");
    Console.WriteLine("that starts from the NEXT annual review, comparing against what gets saved today.\n");

    var headers0 = current[0].Keys.ToList();
    SimpleCsv.WriteWithHeader(snapshotPath, headers0, current.Select(r => headers0.Select(h => r[h])));
    Console.WriteLine($"Wrote baseline snapshot: {snapshotPath}");
    return 0;
}

var priorPath = priorCandidates[0];
Console.WriteLine($"Comparing against prior snapshot: {Path.GetFileName(priorPath)}\n");
var prior = SimpleCsv.ReadWithHeader(priorPath);

var results = new List<Dictionary<string, string>>();
foreach (var goal in current.Select(r => r["goal"]).Distinct())
{
    var curG = current.Where(r => r["goal"] == goal).ToList();
    var priorG = prior.Where(r => r["goal"] == goal).ToList();
    results.Add(ClassifyGoal(goal, curG, priorG, driftThreshold));
}

Directory.CreateDirectory(Path.GetDirectoryName(driftReportPath)!);
var reportHeaders = new[] { "goal", "status", "reason", "bucket_current", "bucket_prior", "max_fund_delta_pct", "n_funds_moved" };
SimpleCsv.WriteWithHeader(driftReportPath, reportHeaders, results.Select(r => reportHeaders.Select(h => r[h])));
Console.WriteLine($"Wrote {driftReportPath}\n");

var icons = new Dictionary<string, string> { ["GREEN"] = "\U0001F7E2", ["YELLOW"] = "\U0001F7E1", ["RED"] = "\U0001F534" };
Console.WriteLine("--- Annual drift report ---");
foreach (var r in results)
    Console.WriteLine($"  {icons[r["status"]]} {r["goal"],-20} {r["status"],-6} — {r["reason"]}");

int nRed = results.Count(r => r["status"] == "RED");
int nYellow = results.Count(r => r["status"] == "YELLOW");
int nGreen = results.Count(r => r["status"] == "GREEN");
Console.WriteLine($"\n{nGreen} unchanged, {nYellow} worth investigating, {nRed} genuinely changed.");
if (nRed > 0)
    Console.WriteLine("-> RED goal(s) present: rebuild that goal's allocation (rerun the transition layer for it).");
else if (nYellow > 0)
    Console.WriteLine("-> Only YELLOW: look at what moved before deciding whether to act — don't auto-rebalance on this alone.");
else
    Console.WriteLine("-> Nothing changed enough to act on. Do nothing, per your own annual-review principle.");

var headers = current[0].Keys.ToList();
SimpleCsv.WriteWithHeader(snapshotPath, headers, current.Select(r => headers.Select(h => r[h])));
Console.WriteLine($"\nWrote this run's snapshot for next year: {snapshotPath}");

return 0;

// ---------------------------------------------------------------------------

static DirectoryInfo FindProjectRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null && !dir.GetFiles("*.csproj").Any())
        dir = dir.Parent;
    return dir ?? new DirectoryInfo(startDir);
}

static Dictionary<string, string> ClassifyGoal(string goal, List<Dictionary<string, string>> current,
    List<Dictionary<string, string>> prior, double threshold)
{
    string? curBucket = current.Count > 0 ? current[0]["bucket"] : null;
    string? priorBucket = prior.Count > 0 ? prior[0]["bucket"] : null;

    var curWeights = current.ToDictionary(r => r["isin"], r => double.Parse(r["weight"], CultureInfo.InvariantCulture));
    var priorWeights = prior.ToDictionary(r => r["isin"], r => double.Parse(r["weight"], CultureInfo.InvariantCulture));
    var allIsins = curWeights.Keys.Union(priorWeights.Keys);

    var deltas = allIsins.ToDictionary(isin => isin,
        isin => curWeights.GetValueOrDefault(isin, 0.0) - priorWeights.GetValueOrDefault(isin, 0.0));
    double maxAbsDelta = deltas.Count > 0 ? deltas.Values.Max(d => Math.Abs(d)) : 0.0;
    var movedFunds = deltas.Where(kv => Math.Abs(kv.Value) > threshold).ToList();

    string status, reason;
    if (priorBucket is not null && curBucket != priorBucket)
    {
        status = "RED";
        reason = $"bucket changed: '{priorBucket}' -> '{curBucket}'";
    }
    else if (movedFunds.Count > 0)
    {
        status = "YELLOW";
        reason = $"{movedFunds.Count} fund(s) moved more than {threshold * 100:F0}pp (max {maxAbsDelta * 100:F1}pp)";
    }
    else
    {
        status = "GREEN";
        reason = $"no fund moved more than {threshold * 100:F0}pp (max observed {maxAbsDelta * 100:F1}pp)";
    }

    return new Dictionary<string, string>
    {
        ["goal"] = goal, ["status"] = status, ["reason"] = reason,
        ["bucket_current"] = curBucket ?? "", ["bucket_prior"] = priorBucket ?? "",
        ["max_fund_delta_pct"] = (maxAbsDelta * 100).ToString("F2", CultureInfo.InvariantCulture),
        ["n_funds_moved"] = movedFunds.Count.ToString(),
    };
}
