using Microsoft.Data.SqlClient;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Tomlyn;
using Tomlyn.Model;

namespace SqlBench.Models {
    public class BenchmarksFile {
        public string ParameterSql { get; set; }
        [DataMember(Name = "cases")]
        public List<BenchmarkCase> BenchmarkCases { get; set; } = new List<BenchmarkCase>();

        public static bool TryReadValidBenchmarks(string connStr, string file, out BenchmarksFile benchmarks) {
            benchmarks = null;
            string fileText;

            try {
                fileText = File.ReadAllText(file);
            } catch (IOException ex) {
                AnsiConsole.WriteException(ex);
                return false;
            }

            var tomlOptions = new TomlModelOptions { };
            if (!Toml.TryToModel(fileText, out benchmarks, out var diag, file, tomlOptions)) {
                AnsiConsole.MarkupLine("[red]Error reading benchmarks toml file.[/]");
                foreach (var item in diag) {
                    if (item.Kind != Tomlyn.Syntax.DiagnosticMessageKind.Error) {
                        continue;
                    }
                    AnsiConsole.MarkupLineInterpolated($"[red]{item.Message}[/]");
                }
                return false;
            }
            foreach (var @case in benchmarks.BenchmarkCases) {
                @case.ConnectionString = @case.ConnectionString ?? connStr;
            }
            return true;
        } 
    }

    public class BenchmarkCase {
        public string Name { get; set; }
        public string ParameterSql { get; set; }
        public string Sql { get; set; }
        public string ConnectionString { get; set; }
    }
}
