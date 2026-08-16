using System.Text;

namespace MfToolkit;

/// <summary>
/// Deliberately minimal CSV reader/writer — handles double-quoted fields with
/// embedded commas, which is all our data needs. Not a general-purpose RFC4180
/// parser; swap for a real library (e.g. CsvHelper NuGet package) if the data
/// ever gets messier than this.
/// </summary>
public static class SimpleCsv
{
    public static List<Dictionary<string, string>> ReadWithHeader(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        using var reader = new StreamReader(path);
        var headerLine = reader.ReadLine();
        if (headerLine is null) return rows;
        var headers = SplitLine(headerLine);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var fields = SplitLine(line);
            var row = new Dictionary<string, string>();
            for (int i = 0; i < headers.Count; i++)
                row[headers[i]] = i < fields.Count ? fields[i] : "";
            rows.Add(row);
        }
        return rows;
    }

    private static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }

    public static string Escape(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }

    public static void WriteWithHeader(string path, IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        writer.WriteLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
            writer.WriteLine(string.Join(",", row.Select(Escape)));
    }
}
