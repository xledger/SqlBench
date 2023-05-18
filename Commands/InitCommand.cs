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
    internal sealed class InitCommand : Command<InitCommand.Settings> {
        public sealed class Settings : CommandSettings {}

        public override int Execute([NotNull] CommandContext context, [NotNull] InitCommand.Settings settings) {
            try {
                using var fs = File.Open("Config.toml", FileMode.CreateNew);
                using var sw = new StreamWriter(fs, Encoding.UTF8);
                sw.WriteLine(@"ConnectionString = ""Data Source=dbhost;Application Name=SqlBench;Initial Catalog=MyDatabase;Integrated Security=True;TrustServerCertificate=True""");
                AnsiConsole.MarkupLine("[green]Example Config.toml created.[/]");
            } catch (IOException) {
                AnsiConsole.MarkupLine("[yellow]Config.toml already exists, leaving it alone.[/]");
            }

            try {
                using var fs = File.Open("BenchCases.toml", FileMode.CreateNew);
                using var sw = new StreamWriter(fs, Encoding.UTF8);
                sw.WriteLine("[current_version]");
                sw.WriteLine("sql = \"\"\"select top 1 *\nfrom sys.tables\n\"\"\"");

                sw.WriteLine("\n[using_cte]");
                sw.WriteLine("sql = \"\"\"with tables as (select top 1 *\nfrom sys.tables\n) select * from tables\"\"\"");
                AnsiConsole.MarkupLine("[green]Example BenchCases.toml created.[/]");
            } catch (IOException) {
                AnsiConsole.MarkupLine("[yellow]BenchCases.toml already exists, leaving it alone.[/]");
            }
            return 0;
        }
    }
}
