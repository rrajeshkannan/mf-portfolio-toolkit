// Step 7 (C# port): turn the frontier into named, concrete portfolios.
// Mirrors python/select_candidate_portfolios.py logic exactly — see that file's
// docstring for the full rationale (why 5 buckets, why anchored on GMV/Max Sharpe/
// top-return specifically, why the stability cross-check only applies to one bucket).
//
// Reads:  ../output/frontier_points.csv
//         ../output/frontier_key_portfolios.csv
//         ../output/frontier_bootstrap_stability.csv (optional)
//         ../data/funds_universe.csv
// Writes: ../output/candidate_portfolios.csv
//
// Usage: dotnet run
//        dotnet run -- --n-buckets 3

using System.Globalization;
using MfCandidates;
using MfToolkit;

var projectDir = FindProjectRoot(AppContext.BaseDirectory);
var repoRoot = projectDir.Parent!.FullName;
var frontierPointsPath = Path.Combine(repoRoot, "output", "frontier_points.csv");
var keyPortfoliosPath = Path.Combine(repoRoot, "output", "frontier_key_portfolios.csv");
var stabilityPath = Path.Combine(repoRoot, "output", "frontier_bootstrap_stability.csv");
var universePath = Path.Combine(repoRoot, "data", "funds_universe.csv");
var outputPath = Path.Combine(repoRoot, "output", "candidate_portfolios.csv");

int nBuckets = 5;
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--n-buckets") nBuckets = int.Parse(args[++i]);

const double UnstableWeightThreshold = 0.05;
const double UnstableInclusionThreshold = 0.30;

if (!File.Exists(frontierPointsPath) || !File.Exists(keyPortfoliosPath))
    throw new FileNotFoundException("Missing frontier outputs — run csharp-frontier first.");

var universe = SimpleCsv.ReadWithHeader(universePath).ToDictionary(r => r["isin"].Trim(), r => r);

var keyRows = SimpleCsv.ReadWithHeader(keyPortfoliosPath);
var isins = keyRows[0].Keys.Where(k => k is not ("portfolio" or "return_pct" or "volatility_pct" or "sharpe")).ToList();

FrontierPoint ParseRow(Dictionary<string, string> row, string returnKey) => new(
    double.Parse(row[returnKey], CultureInfo.InvariantCulture),
    double.Parse(row["volatility_pct"], CultureInfo.InvariantCulture),
    double.Parse(row["sharpe"], CultureInfo.InvariantCulture),
    isins.Select(isin => double.Parse(row[isin], CultureInfo.InvariantCulture)).ToArray());

var gmvRow = ParseRow(keyRows.First(r => r["portfolio"] == "Global Minimum Variance"), "return_pct");
var maxSharpeRow = ParseRow(keyRows.First(r => r["portfolio"] == "Max Sharpe"), "return_pct");

var frontierRows = SimpleCsv.ReadWithHeader(frontierPointsPath).Select(r => ParseRow(r, "target_return_pct"));

// Combined, deduplicated (by volatility), sorted-by-volatility list — matches Python's
// pd.concat + sort_values + drop_duplicates(subset="volatility_pct") exactly, including
// "keep first occurrence" semantics on ties.
var combined = frontierRows.Append(gmvRow).Append(maxSharpeRow)
    .OrderBy(p => p.VolatilityPct)
    .GroupBy(p => p.VolatilityPct)
    .Select(g => g.First())
    .ToList();

var idx = LinspaceIndices(combined.Count, nBuckets);
var selected = idx.Select(i => combined[i]).ToList();
var labels = BuildBucketLabels(selected.Count);

int closestToMaxSharpe = 0;
double bestDiff = double.MaxValue;
for (int i = 0; i < selected.Count; i++)
{
    double diff = Math.Abs(selected[i].VolatilityPct - maxSharpeRow.VolatilityPct);
    if (diff < bestDiff) { bestDiff = diff; closestToMaxSharpe = i; }
}
string maxSharpeLabel = labels[closestToMaxSharpe];

Console.WriteLine($"--- {selected.Count} candidate portfolios (Max Sharpe = bootstrap-validated bucket: '{maxSharpeLabel}') ---");
for (int i = 0; i < selected.Count; i++)
    Console.WriteLine($"  {labels[i],-20} return={selected[i].ReturnPct,6:F2}%  vol={selected[i].VolatilityPct,6:F2}%  sharpe={selected[i].Sharpe:F3}");

string ColName(string label) => label.ToLowerInvariant().Replace(" ", "_") + "_weight";

var fundRows = new List<Dictionary<string, string>>();
for (int f = 0; f < isins.Count; f++)
{
    var isin = isins[f];
    var row = new Dictionary<string, string>
    {
        ["isin"] = isin,
        ["name"] = universe.TryGetValue(isin, out var u) ? u["name"] : "?",
        ["category"] = universe.TryGetValue(isin, out var u2) ? u2["category"] : "?",
    };
    for (int b = 0; b < selected.Count; b++)
        row[ColName(labels[b])] = selected[b].Weights[f].ToString("F6", CultureInfo.InvariantCulture);
    fundRows.Add(row);
}

string maxSharpeCol = ColName(maxSharpeLabel);
var warnings = new List<string>();

if (File.Exists(stabilityPath))
{
    var stability = SimpleCsv.ReadWithHeader(stabilityPath).ToDictionary(r => r["isin"].Trim(), r => r);
    foreach (var row in fundRows)
    {
        var isin = row["isin"];
        if (stability.TryGetValue(isin, out var s))
        {
            row["bootstrap_mean_weight"] = s["mean_weight"];
            row["bootstrap_pct_samples_gt_1pct"] = s["pct_samples_weight_gt_1pct"];

            double w = double.Parse(row[maxSharpeCol], CultureInfo.InvariantCulture);
            double incl = double.Parse(s["pct_samples_weight_gt_1pct"], CultureInfo.InvariantCulture);
            if (w > UnstableWeightThreshold && incl < UnstableInclusionThreshold)
            {
                warnings.Add($"  [caution] {maxSharpeLabel}: {row["name"]} gets {w * 100:F1}% weight, but bootstrap only "
                    + $"included it in {incl * 100:F0}% of resamples.");
            }
        }
        else
        {
            row["bootstrap_mean_weight"] = "";
            row["bootstrap_pct_samples_gt_1pct"] = "";
        }
    }
    Console.WriteLine($"\n  [note] stability cross-check applies to '{maxSharpeLabel}' only (the bootstrap-validated "
        + "Max Sharpe bucket) — other buckets haven't been bootstrap-validated against their own objective.");
}
else
{
    Console.WriteLine("\n  [note] no bootstrap stability file found — skipping the stability cross-check");
}

fundRows = fundRows.OrderByDescending(r => double.Parse(r[maxSharpeCol], CultureInfo.InvariantCulture)).ToList();

var headers = new List<string> { "isin", "name", "category" };
headers.AddRange(labels.Select(ColName));
if (File.Exists(stabilityPath)) headers.AddRange(new[] { "bootstrap_mean_weight", "bootstrap_pct_samples_gt_1pct" });

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
SimpleCsv.WriteWithHeader(outputPath, headers, fundRows.Select(r => headers.Select(h => r.GetValueOrDefault(h, ""))));
Console.WriteLine($"\nWrote {outputPath}");

Console.WriteLine("\n--- Non-trivial holdings per bucket (>1% weight) ---");
foreach (var label in labels)
{
    var col = ColName(label);
    var held = fundRows.Where(r => double.Parse(r[col], CultureInfo.InvariantCulture) > 0.01)
        .OrderByDescending(r => double.Parse(r[col], CultureInfo.InvariantCulture));
    Console.WriteLine($"\n  {label}:");
    foreach (var r in held)
        Console.WriteLine($"    {r["name"],-55} {double.Parse(r[col], CultureInfo.InvariantCulture) * 100,5:F1}%");
}

if (warnings.Count > 0)
{
    Console.WriteLine("\n--- Stability cautions ---");
    foreach (var w in warnings) Console.WriteLine(w);
}

return 0;

// ---------------------------------------------------------------------------

static DirectoryInfo FindProjectRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null && !dir.GetFiles("*.csproj").Any())
        dir = dir.Parent;
    return dir ?? new DirectoryInfo(startDir);
}

static List<int> LinspaceIndices(int count, int n)
{
    if (n <= 1 || count <= 1) return new List<int> { 0 };
    var idxSet = new SortedSet<int>();
    for (int i = 0; i < n; i++)
    {
        double val = (double)(count - 1) * i / (n - 1);
        idxSet.Add((int)Math.Round(val, MidpointRounding.ToEven));
    }
    return idxSet.ToList();
}

static List<string> BuildBucketLabels(int n)
{
    if (n == 3) return new List<string> { "Conservative", "Moderate", "Aggressive" };
    if (n == 5) return new List<string> { "Very Conservative", "Conservative", "Moderate", "Aggressive", "Very Aggressive" };
    return Enumerable.Range(1, n).Select(i => $"Bucket {i} of {n}").ToList();
}
