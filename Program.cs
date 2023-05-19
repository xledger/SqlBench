using Tomlyn;
using System;
using System.Data;
using Tomlyn.Model;
using Newtonsoft.Json;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using Spectre.Console.Cli;
using Spectre.Console;
using SqlBench;

internal class Program {
    private static void Main(string[] args) {
        try {
            var app = new CommandApp();
            
            app.Configure(config => {
                config.PropagateExceptions();

                config.AddCommand<SqlBench.Commands.BenchCommand>("bench")
                .WithDescription("Run benchmarks")
                .WithExample(new[] { "bench" });

                config.AddCommand<SqlBench.Commands.InitCommand>("init")
                .WithDescription("Initialize the current directory with configuration and bench cases sql");
            });
            app.Run(args);
        } catch (UserCausedException ex) {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            foreach (var err in ex.UserErrors) {
                AnsiConsole.MarkupLineInterpolated($"[red]{err}[/]");
            }
            if (ex.Data?.Count > 0) {
                var tree = new Tree("[gold1]Exception Data[/]");
                var table = new Table()
                    .RoundedBorder()
                    .AddColumn("Key")
                    .AddColumn("Value");
                tree.AddNode(table);
                foreach (var k in ex.Data.Keys) {
                    table.AddRow($"[aqua]{k?.ToString()?.EscapeMarkup()}[/]", $"[yellow]{ex.Data[k]?.ToString()?.EscapeMarkup()}[/]");
                }
                AnsiConsole.Write(tree);
            }
        } catch (Exception ex) {
            AnsiConsole.WriteException(ex);
        }
    }
}