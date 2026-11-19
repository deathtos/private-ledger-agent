using System.Text;

namespace PrivateLedgerAgent.Ledger;

/// <summary>RFC 4180 CSV reader that reports the physical line each record starts on.</summary>
public static class CsvParser
{
    public static IEnumerable<(int Line, string[] Fields)> Parse(TextReader reader)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var any = false;
        var line = 1;
        var recordStart = 1;
        int c;

        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); sb.Append('"'); }
                    else inQuotes = false;
                }
                else
                {
                    if (ch == '\n') line++;
                    if (ch != '\r') sb.Append(ch);
                }
                continue;
            }

            switch (ch)
            {
                case '"': inQuotes = true; any = true; break;
                case ',': fields.Add(sb.ToString()); sb.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    fields.Add(sb.ToString()); sb.Clear();
                    if (any || fields[0].Length > 0) yield return (recordStart, fields.ToArray());
                    fields.Clear(); any = false; line++; recordStart = line;
                    break;
                default: sb.Append(ch); any = true; break;
            }
        }

        if (any || sb.Length > 0)
        {
            fields.Add(sb.ToString());
            yield return (recordStart, fields.ToArray());
        }
    }
}
