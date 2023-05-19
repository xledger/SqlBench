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
using SqlBench.Models;
using Microsoft.SqlServer.TransactSql.ScriptDom;

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

        ConfigurationFile ConfigurationFile { get; set; }
        Settings ExecSettings { get; set; }

        Random Rng = new Random(DateTime.UtcNow.Second);

        public override int Execute([NotNull] CommandContext context, [NotNull] Settings settings) {
            ExecSettings = settings;

            if (!ConfigurationFile.TryReadConfigurationFile("Config.toml", out var configurationFile)) {
                return 1;
            }
            ConfigurationFile = configurationFile;

            if (!BenchmarksFile.TryReadValidBenchmarks(ConfigurationFile.ConnectionString, ExecSettings.BenchmarksPath, out BenchmarksFile benchmarks)) {
                return 1;
            }

            if (ExecSettings.VerifyEquality) {
                if (!VerifyEquality(benchmarks)) {
                    return 1;
                }
            }

            var results = RunTests(benchmarks);

            if (ExecSettings.SortResults) {
                results.Sort((a, b) =>
                    a.Runs.Average(r => r.duration.TotalMilliseconds)
                    .CompareTo(b.Runs.Average(r => r.duration.TotalMilliseconds))
                );
            } else {
                results.Sort((a, b) =>
                    benchmarks.BenchmarkCases.IndexOf(a.Case)
                    .CompareTo(benchmarks.BenchmarkCases.IndexOf(b.Case))
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

        Dictionary<string, List<(string paramName, string datatypeStr)>> declaredParamsBySqlString = new Dictionary<string, List<(string paramName, string datatypeStr)>>();

        List<(string paramName, string datatypeStr)> GetDeclaredParams(string sqlStr) {
            if (declaredParamsBySqlString.TryGetValue(sqlStr, out var decls)) {
                return decls;
            }
            var p = new TSql150Parser(false);
            var frag = p.Parse(new StringReader(sqlStr), out var errors);
            if (errors.Count > 0) {
                throw new UserCausedException("Failed to parse parameter_sql",
                    errors.Select(m => $"{m.Line}:{m.Column}: {m.Message}").ToList()) {
                    Data = { ["Sql"] = sqlStr }
                };
            }

            var g = new Sql150ScriptGenerator();
            decls = new();
            var t = new Xledger.Sql.ScopedFragmentTransformer();
            t.VisForDeclareVariableStatement = (t, dv) => {
                foreach (var decl in dv.Declarations) {
                    g.GenerateScript(decl.VariableName, out var varname);
                    g.GenerateScript(decl.DataType, out var datatype);
                    decls.Add((varname, datatype));
                }
            };
            frag.Accept(t);

            declaredParamsBySqlString[sqlStr] = decls;
            return decls;
        }

        void PrepCmd(SqlCommand cmd, BenchmarksFile benchmarks, BenchmarkCase @case) {
            cmd.CommandTimeout = ExecSettings.TimeoutSeconds;
            if (string.IsNullOrWhiteSpace(benchmarks.ParameterSql) && string.IsNullOrWhiteSpace(@case.ParameterSql)) {
                cmd.CommandText = @case.Sql;
            } else {
                var commandText = new StringBuilder();
                var decls = new List<(string paramName, string datatypeStr)>();
                foreach (var paramSql in new[] { benchmarks.ParameterSql, @case.ParameterSql }) {
                    if (!string.IsNullOrWhiteSpace(paramSql)) {
                        commandText.AppendLine(paramSql);
                        decls.AddRange(GetDeclaredParams(paramSql));
                    }
                }
                commandText.AppendLine();
                var sqlBench_Sql = cmd.Parameters.Add("sqlBench_Sql", System.Data.SqlDbType.NVarChar, -1);
                sqlBench_Sql.Value = @case.Sql;

                var sqlBench_Params = cmd.Parameters.Add("sqlBench_Params", System.Data.SqlDbType.NVarChar, -1);
                sqlBench_Params.Value = decls.Select(d => $"{d.paramName} {d.datatypeStr}").StringJoin(",");

                commandText.AppendLine($"exec sp_executesql @sqlBench_Sql, @sqlBench_Params,{decls.Select(d => $"{d.paramName}").StringJoin(",")}");
                cmd.CommandText = commandText.ToString();
            }
        }

        bool VerifyEquality(BenchmarksFile benchmarks) {
            var ok = true;
            var results = new List<JToken>();
            AnsiConsole.Status()
            .Start("Verifying result equality...", ctx => {
                for (int i = 0; i < benchmarks.BenchmarkCases.Count; i++) {
                    var testCase = benchmarks.BenchmarkCases[i];
                    ctx.Status($"Verifying result equality - processing {i + 1} / {benchmarks.BenchmarkCases.Count}: {testCase.Name.Replace("_", " ").EscapeMarkup()}...");
                    using var conn = new SqlConnection(testCase.ConnectionString);
                    using var cmd = conn.CreateCommand();
                    PrepCmd(cmd, benchmarks, testCase);
                    conn.Open();

                    using var rdr = cmd.ExecuteReader();
                    var tables = new JArray();
                    do {
                        if (rdr.FieldCount == 1 && rdr.GetName(0) == JSON_RESULT_COLNAME) {
                            var sb = new StringBuilder();
                            while (rdr.Read()) {
                                sb.Append(rdr.GetString(0));
                            }
                            var obj = JToken.Parse(sb.ToString());
                            results.Add(obj);
                        } else {
                            var table = new JArray();
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
                                table.Add(record);
                            }
                            tables.Add(table);
                        }
                    } while (rdr.NextResult());

                    results.Add(tables);

                    if (results.Count >= 2) {
                        var first = results[0];
                        var last = results[^1];
                        var jdp = new JsonDiffPatch();

                        var patch = jdp.Diff(first, last);
                        // @TODO - correct comparison between json coerced vs plain json results
                        if (patch is not null && patch.Type != JTokenType.Null) {
                            var json = new JsonText(JsonConvert.SerializeObject(patch, Formatting.Indented));
                            AnsiConsole.MarkupLine($"[red]Result from[/] {benchmarks.BenchmarkCases[0].Name.EscapeMarkup()} [red]and[/] {benchmarks.BenchmarkCases[i].Name.EscapeMarkup()} [red]are not equal.[/]");
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

        List<PerfTestResult> RunTests(BenchmarksFile benchmarks) {
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
                    benchmarks.BenchmarkCases
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
                var toProcess = new List<(BenchmarkCase @case, int caseIdx, int runIdx)>();
                for (int i = 0; i < benchmarks.BenchmarkCases.Count; i++) {
                    var @case = benchmarks.BenchmarkCases[i];

                    if (ExecSettings.ReRunAll) {
                        _ = appDbConn.Execute(@"delete from BenchmarkRun
where Name = ? and ConnectionString = ?", @case.Name, @case.ConnectionString);
                    } else {
                        var cmd = new SqlCommand();
                        PrepCmd(cmd, benchmarks, benchmarks.BenchmarkCases[i]);
                        var prevRuns = appDbConn.Query<BenchmarkRun>(@"select * 
from BenchmarkRun 
where Name = ? and ConnectionString = ? and Sql = ?
order by RunAt desc
limit 1", @case.Name, @case.ConnectionString, cmd.CommandText);
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
                        PrepCmd(cmd, benchmarks, @case);
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

        public record PerfTestResultRun(TimeSpan duration);

        public class PerfTestResult {
            public BenchmarkCase Case { get; }
            public PerfTestResult(BenchmarkCase @case) {
                Case = @case;
            }
            public List<PerfTestResultRun> Runs = new();
        }
    }
}
