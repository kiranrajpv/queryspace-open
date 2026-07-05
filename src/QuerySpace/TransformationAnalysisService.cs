using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QuerySpace;

public static class TransformationAnalysisService
{
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    public static TransformationAnalysisReport Analyze(string sql, DataTable table)
    {
        var tags = ParseTags(sql);
        var analysisName = tags.GetValueOrDefault("analysis", "sap-material-transformation");
        var comparePairs = ParsePairs(tags.GetValueOrDefault("compare", ""));
        if (comparePairs.Count == 0)
        {
            comparePairs = InferPairs(table, tags);
        }

        var xrefValuePairs = ParsePairs(tags.GetValueOrDefault("xref-values", ""));
        var keyColumns = SplitList(tags.GetValueOrDefault("keys", ""));
        var rules = SplitList(tags.GetValueOrDefault("rules", "")).ToHashSet(IgnoreCase);
        if (rules.Count == 0)
        {
            foreach (var rule in new[]
            {
                "null-introduced", "value-changed", "numeric-drift", "date-drift", "leading-zero-loss",
                "case-change", "trim-change", "xref-missing", "xref-mismatch", "xref-expired",
                "xref-default-used", "duplicate-key", "postload-mismatch", "distribution-shift"
            })
            {
                rules.Add(rule);
            }
        }

        var numericTolerance = ParseDouble(tags.GetValueOrDefault("numeric-tolerance", "0"));
        var dateToleranceDays = ParseDouble(tags.GetValueOrDefault("date-tolerance-days", "0"));
        var defaultValues = SplitList(tags.GetValueOrDefault("xref-default-values", "UNKNOWN,N/A,UNMAPPED,")).ToHashSet(IgnoreCase);
        var businessDate = ParseDate(tags.GetValueOrDefault("business-date", "")) ?? DateTime.Today;

        var issues = CreateIssuesTable();
        var pairTable = CreatePairTable();
        var xrefTable = CreateXrefTable();

        foreach (var pair in comparePairs)
        {
            if (!HasColumn(table, pair.Left) || !HasColumn(table, pair.Right)) continue;
            AnalyzePair(table, pair.Left, pair.Right, rules, numericTolerance, dateToleranceDays, issues, pairTable);
        }

        AnalyzeCrossReference(table, tags, xrefValuePairs, rules, defaultValues, businessDate, issues, xrefTable);
        AnalyzeKeys(table, keyColumns, tags, rules, issues);
        AnalyzePostLoad(table, tags, comparePairs, rules, issues);
        AnalyzeDistributionShift(table, comparePairs, rules, issues);

        var overview = CreateOverviewTable(table.Rows.Count, analysisName, tags, issues, comparePairs.Count);
        var recommendations = CreateRecommendationsTable(issues);
        var score = ComputeScore(table.Rows.Count, issues);

        return new TransformationAnalysisReport
        {
            AnalysisName = analysisName,
            RowsAnalyzed = table.Rows.Count,
            QualityScore = score,
            Tags = tags,
            Overview = overview,
            Issues = issues,
            ColumnPairs = pairTable,
            CrossReference = xrefTable,
            Recommendations = recommendations
        };
    }

    public static Dictionary<string, string> ParseTags(string sql)
    {
        var tags = new Dictionary<string, string>(IgnoreCase);
        foreach (var rawLine in sql.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            var match = Regex.Match(line, @"^--\s*@(?<key>[A-Za-z0-9_-]+)\s*:\s*(?<value>.*)$");
            if (!match.Success) continue;
            tags[match.Groups["key"].Value.Trim()] = match.Groups["value"].Value.Trim();
        }
        return tags;
    }

    private static void AnalyzePair(DataTable table, string sourceColumn, string targetColumn, HashSet<string> rules, double numericTolerance, double dateToleranceDays, DataTable issues, DataTable pairTable)
    {
        var compared = 0;
        var equal = 0;
        var nullIntroduced = 0;
        var valueChanged = 0;
        var numericDrift = 0;
        var dateDrift = 0;
        var leadingZeroLoss = 0;
        var caseChange = 0;
        var trimChange = 0;
        string? nullExample = null;
        string? changeExample = null;
        string? numericExample = null;
        string? dateExample = null;
        string? zeroExample = null;
        string? caseExample = null;
        string? trimExample = null;

        foreach (DataRow row in table.Rows)
        {
            var source = Cell(row, sourceColumn);
            var target = Cell(row, targetColumn);
            if (IsBlank(source) && IsBlank(target)) continue;
            compared++;
            var normalizedEqual = Normalized(source) == Normalized(target);
            if (rules.Contains("leading-zero-loss") && StripLeadingZeros(source) == StripLeadingZeros(target) && source != target)
            {
                leadingZeroLoss++;
                zeroExample ??= $"{source} -> {target}";
            }
            if (rules.Contains("case-change") && string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && source != target)
            {
                caseChange++;
                caseExample ??= $"{source} -> {target}";
            }
            if (rules.Contains("trim-change") && source.Trim() == target.Trim() && source != target)
            {
                trimChange++;
                trimExample ??= $"'{source}' -> '{target}'";
            }
            if (normalizedEqual)
            {
                equal++;
                continue;
            }

            if (rules.Contains("null-introduced") && !IsBlank(source) && IsBlank(target))
            {
                nullIntroduced++;
                nullExample ??= $"{sourceColumn}={source}; {targetColumn}=<blank>";
            }

            if (rules.Contains("numeric-drift") && TryDecimal(source, out var sourceNumber) && TryDecimal(target, out var targetNumber))
            {
                var diff = Math.Abs(sourceNumber - targetNumber);
                if (diff > (decimal)numericTolerance)
                {
                    numericDrift++;
                    numericExample ??= $"{sourceNumber} -> {targetNumber} (diff {diff})";
                }
            }
            else if (rules.Contains("date-drift") && TryDate(source, out var sourceDate) && TryDate(target, out var targetDate))
            {
                var days = Math.Abs((sourceDate.Date - targetDate.Date).TotalDays);
                if (days > dateToleranceDays)
                {
                    dateDrift++;
                    dateExample ??= $"{sourceDate:yyyy-MM-dd} -> {targetDate:yyyy-MM-dd} ({days:N0} days)";
                }
            }

            if (rules.Contains("value-changed"))
            {
                valueChanged++;
                changeExample ??= $"{source} -> {target}";
            }
        }

        pairTable.Rows.Add(sourceColumn, targetColumn, compared, equal, valueChanged, nullIntroduced, numericDrift, dateDrift, leadingZeroLoss, caseChange, trimChange);
        AddIssueIfAny(issues, "null-introduced", "High", $"{sourceColumn} -> {targetColumn}", nullIntroduced, table.Rows.Count, nullExample, "Transformation produced blank/NULL values from populated ECC source values.");
        AddIssueIfAny(issues, "value-changed", "Medium", $"{sourceColumn} -> {targetColumn}", valueChanged, table.Rows.Count, changeExample, "Validate whether value changes are expected transformation rules or unexpected data loss.");
        AddIssueIfAny(issues, "numeric-drift", "High", $"{sourceColumn} -> {targetColumn}", numericDrift, table.Rows.Count, numericExample, "Check UOM conversion, decimal precision, rounding, and mapping logic.");
        AddIssueIfAny(issues, "date-drift", "Medium", $"{sourceColumn} -> {targetColumn}", dateDrift, table.Rows.Count, dateExample, "Check date conversion, validity range, timezone, and default date handling.");
        AddIssueIfAny(issues, "leading-zero-loss", "Medium", $"{sourceColumn} -> {targetColumn}", leadingZeroLoss, table.Rows.Count, zeroExample, "SAP material numbers often require ALPHA/leading-zero normalization. Confirm MDG expected format.");
        AddIssueIfAny(issues, "case-change", "Low", $"{sourceColumn} -> {targetColumn}", caseChange, table.Rows.Count, caseExample, "Confirm whether uppercase/lowercase normalization is intended.");
        AddIssueIfAny(issues, "trim-change", "Low", $"{sourceColumn} -> {targetColumn}", trimChange, table.Rows.Count, trimExample, "Confirm whether whitespace cleanup is intended.");
    }

    private static void AnalyzeCrossReference(DataTable table, Dictionary<string, string> tags, List<Pair> xrefValuePairs, HashSet<string> rules, HashSet<string> defaultValues, DateTime businessDate, DataTable issues, DataTable xrefTable)
    {
        var xrefPrefix = tags.GetValueOrDefault("xref-prefix", "xref_");
        var xrefColumns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).Where(c => c.StartsWith(xrefPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (xrefColumns.Count == 0 && xrefValuePairs.Count == 0) return;

        var rows = table.Rows.Count;
        var missingXref = 0;
        var defaultUsed = 0;
        var expired = 0;
        var mismatches = 0;
        string? missingExample = null;
        string? defaultExample = null;
        string? expiredExample = null;
        string? mismatchExample = null;

        var validFromColumn = tags.GetValueOrDefault("xref-valid-from", "");
        var validToColumn = tags.GetValueOrDefault("xref-valid-to", "");

        foreach (DataRow row in table.Rows)
        {
            if (rules.Contains("xref-missing") && xrefColumns.Count > 0 && xrefColumns.All(c => IsBlank(Cell(row, c))))
            {
                missingXref++;
                missingExample ??= "All xref columns are blank for a source row.";
            }

            foreach (var pair in xrefValuePairs)
            {
                if (!HasColumn(table, pair.Left) || !HasColumn(table, pair.Right)) continue;
                var transformed = Cell(row, pair.Left);
                var expected = Cell(row, pair.Right);
                if (rules.Contains("xref-mismatch") && !IsBlank(expected) && Normalized(transformed) != Normalized(expected))
                {
                    mismatches++;
                    mismatchExample ??= $"{pair.Left}={transformed}; expected from {pair.Right}={expected}";
                }
                if (rules.Contains("xref-default-used") && defaultValues.Contains(transformed))
                {
                    defaultUsed++;
                    defaultExample ??= $"{pair.Left}={transformed}";
                }
            }

            if (rules.Contains("xref-expired") && HasColumn(table, validFromColumn) && HasColumn(table, validToColumn))
            {
                var fromOk = !TryDate(Cell(row, validFromColumn), out var from) || from.Date <= businessDate.Date;
                var toOk = !TryDate(Cell(row, validToColumn), out var to) || to.Date >= businessDate.Date;
                if (!fromOk || !toOk)
                {
                    expired++;
                    expiredExample ??= $"{validFromColumn}={Cell(row, validFromColumn)}; {validToColumn}={Cell(row, validToColumn)}; business-date={businessDate:yyyy-MM-dd}";
                }
            }
        }

        xrefTable.Rows.Add("Rows with no xref match", missingXref, Percent(missingXref, rows), missingExample ?? "");
        xrefTable.Rows.Add("Rows with xref value mismatch", mismatches, Percent(mismatches, rows), mismatchExample ?? "");
        xrefTable.Rows.Add("Rows using default/unmapped value", defaultUsed, Percent(defaultUsed, rows), defaultExample ?? "");
        xrefTable.Rows.Add("Rows with expired/not-yet-valid xref", expired, Percent(expired, rows), expiredExample ?? "");

        AddIssueIfAny(issues, "xref-missing", "Critical", tags.GetValueOrDefault("xref", "cross-reference"), missingXref, rows, missingExample, "Cross-reference file was not used/found for some ECC source rows. Check xref keys and filter criteria.");
        AddIssueIfAny(issues, "xref-mismatch", "Critical", tags.GetValueOrDefault("xref", "cross-reference"), mismatches, rows, mismatchExample, "Transformed MDG/pre-load value does not match expected cross-reference mapping.");
        AddIssueIfAny(issues, "xref-default-used", "High", tags.GetValueOrDefault("xref", "cross-reference"), defaultUsed, rows, defaultExample, "Transformation fell back to UNKNOWN/N/A/UNMAPPED/default instead of a valid cross-reference value.");
        AddIssueIfAny(issues, "xref-expired", "High", tags.GetValueOrDefault("xref", "cross-reference"), expired, rows, expiredExample, "Cross-reference row is outside valid-from/valid-to date for the business date.");
    }

    private static void AnalyzeKeys(DataTable table, List<string> keyColumns, Dictionary<string, string> tags, HashSet<string> rules, DataTable issues)
    {
        if (!rules.Contains("duplicate-key") || keyColumns.Count == 0 || keyColumns.Any(c => !HasColumn(table, c))) return;
        var groups = table.Rows.Cast<DataRow>()
            .GroupBy(row => string.Join("|", keyColumns.Select(c => Normalized(Cell(row, c)))))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key.Replace("|", "")) && g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ToList();
        var duplicateRows = groups.Sum(g => g.Count());
        AddIssueIfAny(issues, "duplicate-key", "High", string.Join(", ", keyColumns), duplicateRows, table.Rows.Count, groups.FirstOrDefault()?.Key, "Duplicate material/business keys can create duplicate MDG records or ambiguous cross-reference joins.");
    }

    private static void AnalyzePostLoad(DataTable table, Dictionary<string, string> tags, List<Pair> comparePairs, HashSet<string> rules, DataTable issues)
    {
        if (!rules.Contains("postload-mismatch")) return;
        var postPrefix = tags.GetValueOrDefault("postload-prefix", "mdg_");
        var targetPrefix = tags.GetValueOrDefault("target-prefix", tags.GetValueOrDefault("preload-prefix", "transformed_"));
        var count = 0;
        string? example = null;
        foreach (var pair in comparePairs)
        {
            var targetColumn = pair.Right.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase) ? pair.Right : targetPrefix + pair.Right;
            var postColumn = pair.Right.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase)
                ? postPrefix + pair.Right[targetPrefix.Length..]
                : postPrefix + pair.Right;
            if (!HasColumn(table, targetColumn) || !HasColumn(table, postColumn)) continue;
            foreach (DataRow row in table.Rows)
            {
                if (Normalized(Cell(row, targetColumn)) == Normalized(Cell(row, postColumn))) continue;
                count++;
                example ??= $"{targetColumn}={Cell(row, targetColumn)}; {postColumn}={Cell(row, postColumn)}";
            }
        }
        AddIssueIfAny(issues, "postload-mismatch", "Critical", "Pre-load vs MDG post-load", count, table.Rows.Count, example, "MDG loaded value differs from pre-load transformed value. Check load program, MDG derivations, validations, or enrichment rules.");
    }

    private static void AnalyzeDistributionShift(DataTable table, List<Pair> comparePairs, HashSet<string> rules, DataTable issues)
    {
        if (!rules.Contains("distribution-shift")) return;
        foreach (var pair in comparePairs.Take(20))
        {
            if (!HasColumn(table, pair.Left) || !HasColumn(table, pair.Right)) continue;
            if (IsMostlyNumeric(table, pair.Left) || IsMostlyNumeric(table, pair.Right)) continue;
            var leftDistinct = table.Rows.Cast<DataRow>().Select(r => Normalized(Cell(r, pair.Left))).Where(v => v.Length > 0).Distinct(IgnoreCase).Count();
            var rightDistinct = table.Rows.Cast<DataRow>().Select(r => Normalized(Cell(r, pair.Right))).Where(v => v.Length > 0).Distinct(IgnoreCase).Count();
            if (leftDistinct == 0) continue;
            var change = Math.Abs(leftDistinct - rightDistinct) * 100.0 / leftDistinct;
            if (change >= 25)
            {
                AddIssueIfAny(issues, "distribution-shift", "Medium", $"{pair.Left} -> {pair.Right}", (int)Math.Round(change), 100, $"{leftDistinct} distinct source values vs {rightDistinct} transformed values", "Distribution changed substantially after transformation. Check mapping collapse, defaults, and missing cross-reference coverage.");
            }
        }
    }

    private static List<Pair> InferPairs(DataTable table, Dictionary<string, string> tags)
    {
        var result = new List<Pair>();
        var sourcePrefix = tags.GetValueOrDefault("source-prefix", tags.GetValueOrDefault("extract-prefix", "source_"));
        var targetPrefix = tags.GetValueOrDefault("target-prefix", tags.GetValueOrDefault("preload-prefix", "transformed_"));
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToHashSet(IgnoreCase);
        foreach (var source in columns.Where(c => c.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            var suffix = source[sourcePrefix.Length..];
            var target = targetPrefix + suffix;
            if (columns.Contains(target)) result.Add(new Pair(source, target));
        }
        return result;
    }

    private static Dictionary<string, string> ParseSeverity(string value) =>
        ParsePairs(value).ToDictionary(p => p.Left, p => p.Right, IgnoreCase);

    private static List<Pair> ParsePairs(string value)
    {
        var pairs = new List<Pair>();
        foreach (var item in SplitList(value))
        {
            var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
            {
                pairs.Add(new Pair(parts[0], parts[1]));
            }
        }
        return pairs;
    }

    private static List<string> SplitList(string value) =>
        value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

    private static DataTable CreateOverviewTable(int rows, string analysisName, Dictionary<string, string> tags, DataTable issues, int pairCount)
    {
        var table = new DataTable();
        table.Columns.Add("Metric", typeof(string));
        table.Columns.Add("Value", typeof(string));
        table.Rows.Add("Analysis", analysisName);
        table.Rows.Add("Project context", "SAP Material Transformation: ECC extract -> Pre-load transformation -> S/4 MDG post-load validation");
        table.Rows.Add("Rows analyzed", rows.ToString("N0", CultureInfo.InvariantCulture));
        table.Rows.Add("Column pairs compared", pairCount.ToString("N0", CultureInfo.InvariantCulture));
        table.Rows.Add("Issue rows/rules found", issues.Rows.Count.ToString("N0", CultureInfo.InvariantCulture));
        table.Rows.Add("Source/ECC", tags.GetValueOrDefault("source", tags.GetValueOrDefault("extract", "")));
        table.Rows.Add("Target/pre-load", tags.GetValueOrDefault("target", tags.GetValueOrDefault("preload", "")));
        table.Rows.Add("Post-load/MDG", tags.GetValueOrDefault("postload", ""));
        table.Rows.Add("Cross-reference", tags.GetValueOrDefault("xref", ""));
        return table;
    }

    private static DataTable CreateIssuesTable()
    {
        var table = new DataTable();
        table.Columns.Add("Rule", typeof(string));
        table.Columns.Add("Severity", typeof(string));
        table.Columns.Add("Column / scope", typeof(string));
        table.Columns.Add("Count", typeof(int));
        table.Columns.Add("Percent", typeof(string));
        table.Columns.Add("Example", typeof(string));
        table.Columns.Add("What to check", typeof(string));
        return table;
    }

    private static DataTable CreatePairTable()
    {
        var table = new DataTable();
        table.Columns.Add("Source column", typeof(string));
        table.Columns.Add("Transformed column", typeof(string));
        table.Columns.Add("Compared", typeof(int));
        table.Columns.Add("Equal after normalization", typeof(int));
        table.Columns.Add("Value changed", typeof(int));
        table.Columns.Add("Null introduced", typeof(int));
        table.Columns.Add("Numeric drift", typeof(int));
        table.Columns.Add("Date drift", typeof(int));
        table.Columns.Add("Leading-zero loss", typeof(int));
        table.Columns.Add("Case change", typeof(int));
        table.Columns.Add("Trim change", typeof(int));
        return table;
    }

    private static DataTable CreateXrefTable()
    {
        var table = new DataTable();
        table.Columns.Add("Check", typeof(string));
        table.Columns.Add("Count", typeof(int));
        table.Columns.Add("Percent", typeof(string));
        table.Columns.Add("Example", typeof(string));
        return table;
    }

    private static DataTable CreateRecommendationsTable(DataTable issues)
    {
        var table = new DataTable();
        table.Columns.Add("Priority", typeof(int));
        table.Columns.Add("Recommendation", typeof(string));
        var priority = 1;
        foreach (DataRow issue in issues.Rows.Cast<DataRow>().OrderBy(r => SeverityRank(r["Severity"]?.ToString() ?? "")).ThenByDescending(r => Convert.ToInt32(r["Count"], CultureInfo.InvariantCulture)).Take(12))
        {
            table.Rows.Add(priority++, $"{issue["Rule"]}: {issue["What to check"]}");
        }
        if (table.Rows.Count == 0)
        {
            table.Rows.Add(1, "No transformation issues were detected in the analyzed result set. Increase preview/sample rows for stronger confidence.");
        }
        return table;
    }

    private static void AddIssueIfAny(DataTable table, string rule, string severity, string scope, int count, int totalRows, string? example, string whatToCheck)
    {
        if (count <= 0) return;
        table.Rows.Add(rule, severity, scope, count, Percent(count, totalRows), example ?? "", whatToCheck);
    }

    private static double ComputeScore(int rows, DataTable issues)
    {
        if (rows <= 0) return 100;
        double penalty = 0;
        foreach (DataRow issue in issues.Rows)
        {
            var count = Convert.ToInt32(issue["Count"], CultureInfo.InvariantCulture);
            var severity = issue["Severity"]?.ToString() ?? "";
            var weight = severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ? 1.6 :
                severity.Equals("High", StringComparison.OrdinalIgnoreCase) ? 1.1 :
                severity.Equals("Medium", StringComparison.OrdinalIgnoreCase) ? 0.65 : 0.3;
            penalty += Math.Min(35, count * 100.0 / rows) * weight;
        }
        return Math.Round(Math.Max(0, 100 - penalty), 1);
    }

    private static int SeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        _ => 3
    };

    private static bool HasColumn(DataTable table, string column) => !string.IsNullOrWhiteSpace(column) && table.Columns.Contains(column);
    private static string Cell(DataRow row, string column) => !row.Table.Columns.Contains(column) || row[column] == DBNull.Value ? "" : Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? "";
    private static bool IsBlank(string value) => string.IsNullOrWhiteSpace(value) || value.Equals("NULL", StringComparison.OrdinalIgnoreCase) || value.Equals("N/A", StringComparison.OrdinalIgnoreCase) || value.Equals("#N/A", StringComparison.OrdinalIgnoreCase);
    private static string Normalized(string value) => StripLeadingZeros(value.Trim()).ToUpperInvariant();
    private static string StripLeadingZeros(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return trimmed;
        var stripped = trimmed.TrimStart('0');
        return stripped.Length == 0 ? "0" : stripped;
    }

    private static bool TryDecimal(string value, out decimal number) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out number);
    private static bool TryDate(string value, out DateTime date) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date);
    private static DateTime? ParseDate(string value) => TryDate(value, out var date) ? date : null;
    private static double ParseDouble(string value) => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static string Percent(int count, int total) => total <= 0 ? "0.0%" : (count * 100.0 / total).ToString("N1", CultureInfo.InvariantCulture) + "%";
    private static bool IsMostlyNumeric(DataTable table, string column)
    {
        var values = table.Rows.Cast<DataRow>().Select(r => Cell(r, column)).Where(v => !IsBlank(v)).Take(200).ToList();
        return values.Count > 0 && values.Count(v => TryDecimal(v, out _)) >= values.Count * 0.8;
    }

    private sealed record Pair(string Left, string Right);
}
