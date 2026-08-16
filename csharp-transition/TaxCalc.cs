namespace MfTransition;

public class Lot
{
    public DateTime Date;
    public double Units;
    public double Cost;
}

public record SaleTaxResult(double LtGain, double LtUnits, double StGain, double StUnits, double UnitsUnsoldInsufficientLots);

public static class TaxCalc
{
    public const int LtCutoffDays = 365;
    public const int ElssLockInDays = 3 * 365;
    private static readonly string[] ElssHints = { "ELSS", "TAX SAVER" };

    /// <summary>Replays one (investor, isin, folio)'s transaction history and returns the
    /// remaining lots after netting out any historical redemptions/switches — mirrors
    /// compute_transition.py's fifo_lots() exactly.</summary>
    public static List<Lot> FifoLots(List<(DateTime Date, string Investor, string Isin, string Folio, double Units, double Amount)> cashflows,
        string investor, string isin, string folio)
    {
        var txns = cashflows
            .Where(c => c.Investor == investor && c.Isin == isin && c.Folio == folio)
            .OrderBy(c => c.Date)
            .ToList();

        var lots = new List<Lot>();
        foreach (var txn in txns)
        {
            if (txn.Units > 0)
            {
                lots.Add(new Lot { Date = txn.Date, Units = txn.Units, Cost = Math.Abs(txn.Amount) });
            }
            else if (txn.Units < 0)
            {
                double remaining = -txn.Units;
                foreach (var lot in lots)
                {
                    if (remaining <= 1e-6) break;
                    if (lot.Units <= 1e-9) continue;
                    double take = Math.Min(lot.Units, remaining);
                    double costPerUnit = lot.Cost / lot.Units;
                    lot.Cost -= take * costPerUnit;
                    lot.Units -= take;
                    remaining -= take;
                }
                lots = lots.Where(l => l.Units > 1e-6).ToList();
            }
        }
        return lots;
    }

    public static SaleTaxResult EstimateSaleTax(List<Lot> lots, double unitsToSell, double currentValueTotal,
        double currentUnitsTotal, DateTime asOf)
    {
        double pricePerUnit = currentUnitsTotal > 0 ? currentValueTotal / currentUnitsTotal : 0;
        double remaining = unitsToSell;
        double ltCost = 0, ltUnits = 0, stCost = 0, stUnits = 0;

        foreach (var lot in lots)
        {
            if (remaining <= 1e-6) break;
            double take = Math.Min(lot.Units, remaining);
            double costPerUnit = lot.Units > 0 ? lot.Cost / lot.Units : 0;
            double costOfTake = take * costPerUnit;
            bool isLongTerm = (asOf - lot.Date).Days > LtCutoffDays;
            if (isLongTerm) { ltCost += costOfTake; ltUnits += take; }
            else { stCost += costOfTake; stUnits += take; }
            remaining -= take;
        }

        double ltValue = ltUnits * pricePerUnit;
        double stValue = stUnits * pricePerUnit;
        return new SaleTaxResult(ltValue - ltCost, ltUnits, stValue - stCost, stUnits, Math.Max(remaining, 0.0));
    }

    public static bool IsElss(string fundName)
    {
        var upper = fundName.ToUpperInvariant();
        return ElssHints.Any(hint => upper.Contains(hint));
    }

    public static double EarliestUnlockedUnits(List<Lot> lots, DateTime asOf) =>
        lots.Where(l => (asOf - l.Date).Days > ElssLockInDays).Sum(l => l.Units);
}
