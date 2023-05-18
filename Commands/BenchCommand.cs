using Newtonsoft.Json;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Tomlyn;
using Tomlyn.Model;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using SqlBench.Storage;
using Spectre.Console;
using Newtonsoft.Json.Linq;
using System.Text;
using JsonDiffPatchDotNet;
using Spectre.Console.Json;

namespace SqlBench.Commands {
    internal sealed class BenchCommand : Command<BenchCommand.Settings> {
        public sealed class Settings : CommandSettings {
            [Description("Number of iterations to run each benchmark.")]
            [CommandOption("-n|--iterations")]
            [DefaultValue(3)]
            public int Iterations { get; init; }

            [Description("SQL timeout for each benchmark run")]
            [CommandOption("-t|--sql-timeout")]
            [DefaultValue(120)]
            public int TimeoutSeconds { get; init; }

            [Description("Re-run all benchmarks. Skips reusing old results.")]
            [CommandOption("--re-run-all")]
            [DefaultValue(false)]
            public bool ReRunAll { get; init; }

            [Description("Sort results by time")]
            [CommandOption("--sort-results")]
            [DefaultValue(false)]
            public bool SortResults { get; init; }

            [Description("Verify that the cases return identical output before testing." +
                " This will cause an additional execution.")]
            [CommandOption("--verify-result-equality")]
            [DefaultValue(false)]
            public bool VerifyEquality { get; init; }

            [Description("Path to TOML file containing benchmarks to run.")]
            [CommandArgument(0, "<benchmarksTomlPath>")]
            public string BenchmarksPath { get; init; }

            public override ValidationResult Validate() {
                if (Iterations <= 0) {
                    return ValidationResult.Error("Iterations must be a positive integer.");
                }

                if (TimeoutSeconds < 0) {
                    return ValidationResult.Error("Sql timeout can't be negative.");
                }

                if (!File.Exists(BenchmarksPath)) {
                    return ValidationResult.Error($"benchmarksTomlPath \"{BenchmarksPath}\" does not exist.");
                }
                return ValidationResult.Success();
            }
        }

        Settings ExecSettings { get; set; }

        Random Rng = new Random(DateTime.UtcNow.Second);

        public override int Execute([NotNull] CommandContext context, [NotNull] Settings settings) {
            ExecSettings = settings;

            var config = Toml.ToModel(File.ReadAllText("Config.toml"));
            var connStr = config["ConnectionString"] as string;
            if (string.IsNullOrWhiteSpace(connStr)) {
                throw new ArgumentException("Config.toml/ConnectionString cannot be blank");
            }

            var testCases = GetTestCases(connStr);

            if (ExecSettings.VerifyEquality) {
                if (!VerifyEquality(testCases)) {
                    return 1;
                }
            }

            var results = RunTests(testCases);

            if (ExecSettings.SortResults) {
                results.Sort((a, b) =>
                    a.Runs.Average(r => r.duration.TotalMilliseconds)
                    .CompareTo(b.Runs.Average(r => r.duration.TotalMilliseconds))
                );
            } else {
                results.Sort((a, b) =>
                    Array.IndexOf(testCases, a.Case)
                    .CompareTo(Array.IndexOf(testCases, b.Case))
                );
            }

            AnsiConsole.Write(
                new BarChart()
                .Width(80)
                .Label("[green bold underline]Time taken in ms[/]")
                .CenterLabel()
                .AddItems(
                    results
                    .Zip(Colors()),
                    (item) => new BarChartItem(
                        label: item.First.Case.Name.Replace("_", " "),
                        value: Math.Round(item.First.Runs.Average(r => r.duration.TotalMilliseconds)),
                        color: item.Second)
                )
            );

            return 0;
        }

        const string JSON_RESULT_COLNAME = "JSON_F52E2B61-18A1-11d1-B105-00805F49916B";

        bool VerifyEquality(PerfTestCase[] testCases) {
            var ok = true;
            var results = new List<JToken>();
            AnsiConsole.Status()
            .Start("Verifying result equality...", ctx => {
                for (int i = 0; i < testCases.Length; i++) {
                    var testCase = testCases[i];
                    ctx.Status($"Verifying result equality - processing {i + 1} / {testCases.Length}: {testCase.Name.Replace("_", " ").EscapeMarkup()}...");
                    using var conn = new SqlConnection(testCase.ConnectionString);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandTimeout = ExecSettings.TimeoutSeconds;
                    cmd.CommandText = testCase.Sql;
                    conn.Open();
                    
                    using var rdr = cmd.ExecuteReader();
                    if (rdr.FieldCount == 1 && rdr.GetName(0) == JSON_RESULT_COLNAME) {
                        var sb = new StringBuilder();
                        while (rdr.Read()) {
                            sb.Append(rdr.GetString(0));
                        }
                        var obj = JToken.Parse(sb.ToString());
                        results.Add(obj);
                    } else {
                        var result = new JArray();
                        var origKeys = Enumerable.Range(0, rdr.FieldCount).Select(rdr.GetName).ToArray();
                        var keyFreqs = new Dictionary<string, int>();
                        var addIntSuffix = new List<(int idx, int sfx)>();
                        for (int j = 0; j < origKeys.Length; j++) {
                            keyFreqs.TryGetValue(origKeys[j], out var count);
                            if (count > 0) {
                                addIntSuffix.Add((idx: j, sfx: count + 1));
                            }
                            keyFreqs[origKeys[j]] = count + 1;
                        }
                        var keys = origKeys.ToArray();
                        foreach ((var idx, var sfx) in addIntSuffix) {
                            keys[idx] = $"{keys[idx]}{sfx}";
                        }

                        while (rdr.Read()) {
                            var record = new JObject();
                            for (int j = 0; j < keys.Length; j++) {
                                var v = rdr.GetValue(j);
                                var jv = Convert.IsDBNull(v) ? JValue.CreateNull() : JValue.FromObject(v);
                                record.Add(keys[j], jv);
                            }
                            result.Add(record);
                        }
                        results.Add(result);
                    }

                    if (results.Count >= 2) {
                        var first = results[0];
                        var last = results[^1];
                        var jdp = new JsonDiffPatch();

                        var patch = jdp.Diff(first, last);
                        // @TODO - correct comparison between json coerced vs plain json results
                        if (patch is not null && patch.Type != JTokenType.Null) {
                            var json = new JsonText(JsonConvert.SerializeObject(patch, Formatting.Indented));
                            AnsiConsole.MarkupLine($"[red]Result from[/] {testCases[0].Name.EscapeMarkup()} [red]and[/] {testCases[i].Name.EscapeMarkup()} [red]are not equal.[/]");
                            AnsiConsole.Write(
                                new Panel(json)
                                    .Header($"Diff:")
                                    .Collapse()
                                    .RoundedBorder()
                                    .BorderColor(Color.Yellow));
                            ok = false;
                        }
                    }
                }
            });
            if (ok) {
                AnsiConsole.MarkupLine($"[green]All results were equal[/]");
            }
            return ok;
        }

        IEnumerable<Color> Colors() {
            while (true) {
                yield return Color.Yellow;
                yield return Color.LightGreen_1;
                yield return Color.Red;
                yield return Color.Aquamarine1;
                yield return Color.Orange1;
                yield return Color.Fuchsia;
                yield return Color.LightSalmon1;
                yield return Color.Aqua;
                yield return Color.Gold1;
            }
        }

        List<PerfTestResult> RunTests(PerfTestCase[] testCases) {
            using var appDbConn = AppDb.GetConnection();

            var results = new List<PerfTestResult>();
            AnsiConsole.MarkupLine("Running test cases...");
            var s = new SpinnerColumn();
            
            AnsiConsole.Progress()
            .Columns(new ProgressColumn[] {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                s,
            }).Start(ctx => {
                var progressTasks =
                    testCases
                    .Zip(Colors())
                    .Select(t => {
                        var task = ctx.AddTask(
                            $"[rgb({t.Second.R},{t.Second.G},{t.Second.B})]{t.First.Name.Replace("_", " ").EscapeMarkup()}[/]", 
                            false, 
                            ExecSettings.Iterations);
                        return task;
                    })
                    .ToArray();

                var swTotal = Stopwatch.StartNew();
                var toProcess = new List<(PerfTestCase @case, int caseIdx, int runIdx)>();
                for (int i = 0; i < testCases.Length; i++) {
                    var @case = testCases[i];

                    if (ExecSettings.ReRunAll) {
                        _ = appDbConn.Execute(@"delete from BenchmarkRun
where Name = ? and ConnectionString = ?", @case.Name, @case.ConnectionString);
                    } else {
                        var prevRuns = appDbConn.Query<BenchmarkRun>(@"select * 
from BenchmarkRun 
where Name = ? and ConnectionString = ? and Sql = ?
order by RunAt desc
limit 1", @case.Name, @case.ConnectionString, @case.Sql);
                        if (prevRuns.Count == 1) {
                            var prevRun = prevRuns[0];
                            var result = new PerfTestResult(@case);
                            try {
                                var runs = JsonConvert.DeserializeObject<List<PerfTestResultRun>>(prevRun.ResultsJson);
                                result.Runs = runs;
                                results.Add(result);
                                progressTasks[i].Increment(ExecSettings.Iterations);
                                continue;
                            } catch {
                            }
                        }
                    }
                    for (int j = 0; j < ExecSettings.Iterations; j++) {
                        toProcess.Add((@case, i, j));
                    }
                }

                toProcess.Sort((a, b) => Rng.Next(100).CompareTo(50));

                foreach ((var @case, var caseIdx, var idx) in toProcess) {
                    var progTask = progressTasks[caseIdx];
                    if (!progTask.IsStarted) {
                        progTask.StartTask();
                    }
                    using (var conn = new SqlConnection(@case.ConnectionString)) {
                        conn.Open();
                        var result = new PerfTestResult(@case);
                        using var cmd = conn.CreateCommand();
                        cmd.CommandTimeout = ExecSettings.TimeoutSeconds;
                        cmd.CommandText = @case.Sql;
                        var sw = Stopwatch.StartNew();
                        using var rdr = cmd.ExecuteReader();
                        while (rdr.Read()) { } // consume whole reader.
                        sw.Stop();
                        result.Runs.Add(new PerfTestResultRun(sw.Elapsed));

                        
                        results.Add(result);
                        progTask.Increment(1);
                        //AnsiConsole.MarkupLine($"Ran {@case.Name.EscapeMarkup()}");
                    }
                }
                swTotal.Stop();
            });

            results = results.GroupBy(
                r => r.Case,
                r => r.Runs,
                (@case, runs) => new PerfTestResult(@case) {
                    Runs = runs.Aggregate(new List<PerfTestResultRun>(), (acc, e) => {
                        acc.AddRange(e);
                        return acc;
                    })
                }).ToList();
            foreach (var result in results) {
                appDbConn.Insert(new BenchmarkRun {
                    Name = result.Case.Name,
                    ConnectionString = result.Case.ConnectionString,
                    Sql = result.Case.Sql,
                    ResultsJson = JsonConvert.SerializeObject(result.Runs),
                    RunAt = DateTime.UtcNow
                });
            }
            return results;
        }

        PerfTestCase[] GetTestCases(string connStr) {
            var casesT = Toml.ToModel(File.ReadAllText(ExecSettings.BenchmarksPath));
            var cases = new List<PerfTestCase>();
            foreach (var kvp in casesT) {
                if (!(kvp.Value is TomlTable caseObj)) {
                    throw new ArgumentException("Expected each top level key to be an object.");
                }
                var sql = caseObj["sql"] as string;
                if (string.IsNullOrWhiteSpace(sql)) {
                    throw new ArgumentException("Expected each top level key to to have a non-empty \"sql\" key.");
                }
                cases.Add(new PerfTestCase(kvp.Key, sql, connStr));
            }

            return cases.ToArray();
        }

        public record PerfTestCase(string Name, string Sql, string ConnectionString);

        public record PerfTestResultRun(TimeSpan duration);

        public class PerfTestResult {
            public PerfTestCase Case { get; }
            public PerfTestResult(PerfTestCase @case) {
                Case = @case;
            }
            public List<PerfTestResultRun> Runs = new();
        }
    }
}
