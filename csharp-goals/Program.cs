// Step 8 (C# port): goal-mapping overlay. Mirrors python/map_goals_to_portfolios.py
// exactly — same horizon thresholds, same flexibility-bump logic, same caveats about
// this being an all-equity universe. See that file's docstring for the full rationale.
//
// Reads:  ../data/goals.csv
//         ../output/candidate_portfolios.csv
// Writes: ../output/goal_bucket_assignment.csv
//         ../output/goal_portfolio_mapping.csv
//
// Usage: dotnet run
//        dotnet run -- --as-of 2027-01-01

using System.Globalization;
using MfToolkit;

var projectDir = FindProjectRoot(AppContext.BaseDirectory);
var repoRoot = projectDir.Parent!.FullName;
var goalsPath = Path.Combine(repoRoot, "data", "goals.csv");
var candidatesPath = Path.Combine(repoRoot, "output", "candidate_portfolios.csv");
var outputDir = Path.Combine(repoRoot, "output");

DateTime asOf = DateTime.Now;
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--as-of") asOf = DateTime.ParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture);

// (min_years_inclusive, bucket_name) — checked top-down, first match wins
var horizonThresholds = new (double MinYears, string Bucket)[]
{
    (10, "very_aggressive"),
    (7, "aggressive"),
    (4, "moderate"),
    (2, "conservative"),
    (0, "very_conservative"),
};

var defaultHorizonYearsIfNoDate = new Dictionary<string, double>
{
    ["Marriage_HomeLoan"] = 10.0,
    ["Stitch_Kutti"] = 10.0,
};

var bucketOrder = new[] { "very_conservative", "conservative", "moderate", "aggressive", "very_aggressive" };

(string FinalBucket, string HorizonBucket) AssignBucket(double horizonYears, string flexibility)
{
    string horizonBucket = "very_conservative";
    foreach (var (minYears, bucket) in horizonThresholds)
    {
        if (horizonYears >= minYears) { horizonBucket = bucket; break; }
    }

    string finalBucket = horizonBucket;
    if (flexibility.Trim().ToLowerInvariant() == "high")
    {
        int idx = Array.IndexOf(bucketOrder, horizonBucket);
        finalBucket = bucketOrder[Math.Min(idx + 1, bucketOrder.Length - 1)];
    }
    return (finalBucket, horizonBucket);
}

if (!File.Exists(goalsPath)) throw new FileNotFoundException($"{goalsPath} not found");
if (!File.Exists(candidatesPath)) throw new FileNotFoundException($"{candidatesPath} not found — run csharp-candidates first");

var goals = SimpleCsv.ReadWithHeader(goalsPath);
var candidates = SimpleCsv.ReadWithHeader(candidatesPath);

var weightCols = candidates[0].Keys.Where(k => k.EndsWith("_weight") && !k.StartsWith("bootstrap")).ToList();
var bucketNames = weightCols.Select(c => c.Replace("_weight", "")).Distinct().OrderBy(x => x).ToList();

Console.WriteLine($"As-of date: {asOf:yyyy-MM-dd}");
Console.WriteLine($"Available buckets: [{string.Join(", ", bucketNames)}]\n");

var mappingRows = new List<Dictionary<string, string>>();
var shortHorizonWarnings = new List<string>();

foreach (var goal in goals)
{
    var name = goal["goal"];
    var targetDateStr = goal.GetValueOrDefault("target_date", "").Trim();

    double horizonYears;
    string dateSource;
    if (!string.IsNullOrEmpty(targetDateStr))
    {
        var targetDate = DateTime.ParseExact(targetDateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        horizonYears = (targetDate - asOf).Days / 365.25;
        dateSource = "target_date in goals.csv";
    }
    else
    {
        horizonYears = defaultHorizonYearsIfNoDate.GetValueOrDefault(name, 7.0);
        dateSource = $"no target_date on file — using default assumption of {horizonYears}y";
    }

    var flexibility = goal.GetValueOrDefault("flexibility", "low");
    var (bucket, horizonBucket) = AssignBucket(horizonYears, flexibility);

    var adjustmentNote = bucket == horizonBucket ? "" : $" (bumped from horizon-implied '{horizonBucket}' — high flexibility)";
    Console.WriteLine($"{name,-20} horizon={horizonYears,5:F1}y ({dateSource}) -> bucket: {bucket}{adjustmentNote}");

    if (horizonYears < 4)
    {
        shortHorizonWarnings.Add($"  [caution] {name}: {horizonYears:F1}y horizon mapped to an ALL-EQUITY bucket "
            + $"('{bucket}'). Consider whether debt/FD instruments outside this pipeline are more appropriate "
            + "for money needed this soon.");
    }

    mappingRows.Add(new Dictionary<string, string>
    {
        ["goal"] = name,
        ["horizon_years"] = Math.Round(horizonYears, 2).ToString(CultureInfo.InvariantCulture),
        ["horizon_implied_bucket"] = horizonBucket,
        ["bucket_assigned"] = bucket,
        ["flexibility"] = flexibility,
        ["date_confidence"] = goal.GetValueOrDefault("date_confidence", ""),
    });
}

var detailRows = new List<Dictionary<string, string>>();
foreach (var mapping in mappingRows)
{
    var goalName = mapping["goal"];
    var bucket = mapping["bucket_assigned"];
    var weightCol = $"{bucket}_weight";

    foreach (var fundRow in candidates)
    {
        double w = double.Parse(fundRow[weightCol], CultureInfo.InvariantCulture);
        if (w > 0.001)
        {
            detailRows.Add(new Dictionary<string, string>
            {
                ["goal"] = goalName, ["bucket"] = bucket, ["isin"] = fundRow["isin"],
                ["name"] = fundRow["name"], ["category"] = fundRow["category"],
                ["weight"] = w.ToString("F6", CultureInfo.InvariantCulture),
            });
        }
    }
}

Directory.CreateDirectory(outputDir);
var assignmentHeaders = new[] { "goal", "horizon_years", "horizon_implied_bucket", "bucket_assigned", "flexibility", "date_confidence" };
SimpleCsv.WriteWithHeader(Path.Combine(outputDir, "goal_bucket_assignment.csv"), assignmentHeaders,
    mappingRows.Select(r => assignmentHeaders.Select(h => r[h])));

var detailHeaders = new[] { "goal", "bucket", "isin", "name", "category", "weight" };
SimpleCsv.WriteWithHeader(Path.Combine(outputDir, "goal_portfolio_mapping.csv"), detailHeaders,
    detailRows.Select(r => detailHeaders.Select(h => r[h])));

Console.WriteLine($"\nWrote {Path.Combine(outputDir, "goal_bucket_assignment.csv")}");
Console.WriteLine($"Wrote {Path.Combine(outputDir, "goal_portfolio_mapping.csv")}");

Console.WriteLine("\n--- Fund allocation per goal ---");
foreach (var mapping in mappingRows)
{
    var goalName = mapping["goal"];
    Console.WriteLine($"\n  {goalName} (bucket: {mapping["bucket_assigned"]}):");
    var goalDetail = detailRows.Where(r => r["goal"] == goalName)
        .OrderByDescending(r => double.Parse(r["weight"], CultureInfo.InvariantCulture));
    foreach (var r in goalDetail)
        Console.WriteLine($"    {r["name"],-55} {double.Parse(r["weight"], CultureInfo.InvariantCulture) * 100,5:F1}%");
}

if (shortHorizonWarnings.Count > 0)
{
    Console.WriteLine("\n--- Short-horizon cautions ---");
    foreach (var w in shortHorizonWarnings) Console.WriteLine(w);
}

return 0;

static DirectoryInfo FindProjectRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null && !dir.GetFiles("*.csproj").Any())
        dir = dir.Parent;
    return dir ?? new DirectoryInfo(startDir);
}
