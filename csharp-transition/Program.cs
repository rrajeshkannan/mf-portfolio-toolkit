// Step 9 (C# port): the tax-aware transition layer. Mirrors python/compute_transition.py
// exactly — same FIFO cost-basis logic, same LTCG/STCG rates (FY2025-26/26-27, check
// before trusting years from now), same ELSS lock-in handling, same Edu_A-style
// UNMAPPED exclusion. See that file's docstring for full rationale.
//
// Reads:  ../data/current_holdings.csv, ../data/goal_tag_mapping.csv,
//         ../data/cashflows_log.csv, ../output/goal_portfolio_mapping.csv,
//         ../data/funds_universe.csv
// Writes: ../output/transition_actions.csv
//         ../output/transition_edu_a_pending.csv (only if any holding is UNMAPPED)
//
// Usage: dotnet run
//        dotnet run -- --as-of 2027-04-01

using System.Globalization;
using MfToolkit;
using MfTransition;

var projectDir = FindProjectRoot(AppContext.BaseDirectory);
var repoRoot = projectDir.Parent!.FullName;
var holdingsPath = Path.Combine(repoRoot, "data", "current_holdings.csv");
var goalTagMapPath = Path.Combine(repoRoot, "data", "goal_tag_mapping.csv");
var cashflowsPath = Path.Combine(repoRoot, "data", "cashflows_log.csv");
var goalMappingPath = Path.Combine(repoRoot, "output", "goal_portfolio_mapping.csv");
var universePath = Path.Combine(repoRoot, "data", "funds_universe.csv");
var outputDir = Path.Combine(repoRoot, "output");

const double LtcgExemptionPerPerson = 125_000;
const double LtcgRate = 0.125;
const double StcgRate = 0.20;
const double CessRate = 0.04;

DateTime asOf = DateTime.Now;
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--as-of") asOf = DateTime.ParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture);

foreach (var p in new[] { holdingsPath, goalTagMapPath, cashflowsPath, goalMappingPath, universePath })
    if (!File.Exists(p)) throw new FileNotFoundException($"{p} not found");

var holdings = SimpleCsv.ReadWithHeader(holdingsPath);
var goalTagMap = SimpleCsv.ReadWithHeader(goalTagMapPath).ToDictionary(r => r["current_goal_tag"], r => r["pipeline_goal"]);
var cashflows = SimpleCsv.ReadWithHeader(cashflowsPath).Select(r => (
    Date: DateTime.Parse(r["transaction_date"], CultureInfo.InvariantCulture),
    Investor: r["investor"],
    Isin: r["isin"],
    Folio: r["folio"],
    Units: double.Parse(r["units"], CultureInfo.InvariantCulture),
    Amount: double.Parse(r["amount"], CultureInfo.InvariantCulture)
)).ToList();
var goalTargets = SimpleCsv.ReadWithHeader(goalMappingPath);
var universe = SimpleCsv.ReadWithHeader(universePath).ToDictionary(r => r["isin"].Trim(), r => r["name"]);

foreach (var h in holdings)
    h["pipeline_goal"] = goalTagMap.GetValueOrDefault(h["goal_tag"], "UNMAPPED");

var pending = holdings.Where(h => h["pipeline_goal"] == "UNMAPPED").ToList();
if (pending.Count > 0)
{
    var pendingHeaders = holdings[0].Keys.ToList();
    var pendingPath = Path.Combine(outputDir, "transition_edu_a_pending.csv");
    Directory.CreateDirectory(outputDir);
    SimpleCsv.WriteWithHeader(pendingPath, pendingHeaders, pending.Select(h => pendingHeaders.Select(k => h[k])));
    double pendingValue = pending.Sum(h => double.Parse(h["value"], CultureInfo.InvariantCulture));
    Console.WriteLine($"[note] {pending.Count} holdings worth INR {pendingValue:N0} are UNMAPPED "
        + $"(goal_tag_mapping.csv) — written to {pendingPath}, excluded from target comparison.");
}

var mapped = holdings.Where(h => h["pipeline_goal"] != "UNMAPPED").ToList();

var actionRows = new List<Dictionary<string, string>>();

foreach (var goalGroup in mapped.GroupBy(h => h["pipeline_goal"]))
{
    var goal = goalGroup.Key;
    var goalHoldings = goalGroup.ToList();
    double goalTotalValue = goalHoldings.Sum(h => double.Parse(h["value"], CultureInfo.InvariantCulture));
    var targets = goalTargets.Where(r => r["goal"] == goal)
        .ToDictionary(r => r["isin"], r => double.Parse(r["weight"], CultureInfo.InvariantCulture));

    // group by (investor, isin) -> summed units, value
    var currentByFund = goalHoldings
        .GroupBy(h => (h["investor"], h["isin"]))
        .Select(g => new
        {
            Investor = g.Key.Item1,
            Isin = g.Key.Item2,
            Units = g.Sum(x => double.Parse(x["units"], CultureInfo.InvariantCulture)),
            Value = g.Sum(x => double.Parse(x["value"], CultureInfo.InvariantCulture)),
        })
        .ToList();

    var allIsins = currentByFund.Select(x => x.Isin).Union(targets.Keys).Distinct();

    foreach (var isin in allIsins)
    {
        var fundName = universe.GetValueOrDefault(isin, "?");
        double targetWeight = targets.GetValueOrDefault(isin, 0.0);
        double targetValue = goalTotalValue * targetWeight;

        var fundRows = currentByFund.Where(x => x.Isin == isin).ToList();
        if (fundRows.Count == 0)
        {
            actionRows.Add(new Dictionary<string, string>
            {
                ["goal"] = goal, ["investor"] = "(any / new folio)", ["isin"] = isin, ["name"] = fundName,
                ["current_value"] = "0.00", ["target_value"] = targetValue.ToString("F2", CultureInfo.InvariantCulture),
                ["delta"] = targetValue.ToString("F2", CultureInfo.InvariantCulture), ["action"] = "BUY",
                ["lt_gain"] = "", ["st_gain"] = "", ["tax_estimate"] = "", ["elss_locked_units"] = "",
                ["note"] = "New position — no existing holding for this goal.",
            });
            continue;
        }

        double sumValueForIsin = fundRows.Sum(x => x.Value);
        foreach (var fr in fundRows)
        {
            double investorShare = sumValueForIsin > 0 ? fr.Value / sumValueForIsin : 0;
            double investorTargetValue = targetValue * investorShare;
            double delta = investorTargetValue - fr.Value;

            string note = "";
            double? ltGain = null, stGain = null, elssLocked = null;

            if (delta < -1)
            {
                var folios = holdings
                    .Where(h => h["investor"] == fr.Investor && h["isin"] == isin && h["pipeline_goal"] == goal)
                    .Select(h => h["folio"]).Distinct().ToList();

                var allLots = new List<Lot>();
                foreach (var folio in folios)
                    allLots.AddRange(TaxCalc.FifoLots(cashflows, fr.Investor, isin, folio));

                double pricePerUnit = fr.Units > 0 ? fr.Value / fr.Units : 0;
                double unitsToSell = pricePerUnit > 0 ? -delta / pricePerUnit : 0;

                if (TaxCalc.IsElss(fundName))
                {
                    double unlockedUnits = TaxCalc.EarliestUnlockedUnits(allLots, asOf);
                    elssLocked = Math.Max(fr.Units - unlockedUnits, 0.0);
                    if (unitsToSell > unlockedUnits)
                    {
                        note = $"ELSS lock-in limits sale: only {unlockedUnits:F1} of {fr.Units:F1} units unlocked. ";
                        unitsToSell = Math.Min(unitsToSell, unlockedUnits);
                    }
                }

                if (unitsToSell > 0.01)
                {
                    var tax = TaxCalc.EstimateSaleTax(allLots, unitsToSell, fr.Value, fr.Units, asOf);
                    ltGain = tax.LtGain; stGain = tax.StGain;
                    if (tax.UnitsUnsoldInsufficientLots > 0.01)
                        note += $"[warn] {tax.UnitsUnsoldInsufficientLots:F1} units had no matching purchase lot — cost basis may be incomplete. ";
                }
            }

            string action = delta < -1 ? "SELL" : (delta > 1 ? "BUY" : "HOLD");
            actionRows.Add(new Dictionary<string, string>
            {
                ["goal"] = goal, ["investor"] = fr.Investor, ["isin"] = isin, ["name"] = fundName,
                ["current_value"] = fr.Value.ToString("F2", CultureInfo.InvariantCulture),
                ["target_value"] = investorTargetValue.ToString("F2", CultureInfo.InvariantCulture),
                ["delta"] = delta.ToString("F2", CultureInfo.InvariantCulture),
                ["action"] = action,
                ["lt_gain"] = ltGain?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                ["st_gain"] = stGain?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                ["tax_estimate"] = "",
                ["elss_locked_units"] = elssLocked?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                ["note"] = note,
            });
        }
    }
}

Console.WriteLine("\n--- Per-investor LTCG exemption usage (this transition run only) ---");
foreach (var investor in actionRows.Select(r => r["investor"]).Distinct().Where(i => i != "(any / new folio)"))
{
    var invRows = actionRows.Where(r => r["investor"] == investor && r["action"] == "SELL").ToList();
    double totalLtGain = invRows.Sum(r => string.IsNullOrEmpty(r["lt_gain"]) ? 0 : double.Parse(r["lt_gain"], CultureInfo.InvariantCulture));
    double totalStGain = invRows.Sum(r => string.IsNullOrEmpty(r["st_gain"]) ? 0 : double.Parse(r["st_gain"], CultureInfo.InvariantCulture));
    double taxableLt = Math.Max(0, totalLtGain - LtcgExemptionPerPerson);
    double ltTax = taxableLt * LtcgRate;
    double stTax = totalStGain * StcgRate;
    double totalTax = (ltTax + stTax) * (1 + CessRate);
    Console.WriteLine($"  {investor}: LT gain=INR {totalLtGain:N0}  ST gain=INR {totalStGain:N0}  est. tax (incl. cess)=INR {totalTax:N0}");
}

Directory.CreateDirectory(outputDir);
var actionHeaders = new[] { "goal", "investor", "isin", "name", "current_value", "target_value", "delta", "action", "lt_gain", "st_gain", "tax_estimate", "elss_locked_units", "note" };
var actionsPath = Path.Combine(outputDir, "transition_actions.csv");
SimpleCsv.WriteWithHeader(actionsPath, actionHeaders, actionRows.Select(r => actionHeaders.Select(h => r[h])));
Console.WriteLine($"\nWrote {actionsPath} ({actionRows.Count} rows)");

Console.WriteLine("\n--- Actions by goal ---");
foreach (var goal in actionRows.Select(r => r["goal"]).Distinct())
{
    Console.WriteLine($"\n  {goal}:");
    var g = actionRows.Where(r => r["goal"] == goal)
        .OrderBy(r => double.Parse(r["delta"], CultureInfo.InvariantCulture));
    foreach (var r in g)
    {
        Console.WriteLine($"    [{r["action"],-4}] {r["name"],-50} current=INR{double.Parse(r["current_value"], CultureInfo.InvariantCulture),10:N0}  "
            + $"target=INR{double.Parse(r["target_value"], CultureInfo.InvariantCulture),10:N0}  delta=INR{double.Parse(r["delta"], CultureInfo.InvariantCulture),10:N0}  {r["note"]}");
    }
}

return 0;

static DirectoryInfo FindProjectRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null && !dir.GetFiles("*.csproj").Any())
        dir = dir.Parent;
    return dir ?? new DirectoryInfo(startDir);
}
