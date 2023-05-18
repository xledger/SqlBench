using Tomlyn;
using System;
using System.Data;
using Tomlyn.Model;
using Newtonsoft.Json;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using Spectre.Console.Cli;
using Spectre.Console;

internal class Program {
    private static void Main(string[] args) {
        try {
            var app = new CommandApp();
            app.Configure(config => {
                config.AddCommand<SqlBench.Commands.BenchCommand>("bench")
                .WithDescription("Run benchmarks")
                .WithExample(new[] { "bench" });

                config.AddCommand<SqlBench.Commands.InitCommand>("init")
                .WithDescription("Initialize the current directory with configuration and bench cases sql");
            });
            app.Run(args);
        } catch (Exception ex) {
            AnsiConsole.WriteException(ex);
        }
    }
}