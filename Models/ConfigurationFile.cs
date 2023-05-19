using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tomlyn;

namespace SqlBench.Models {
    public class ConfigurationFile {
        public string ConnectionString { get; set; }

        public static bool TryReadConfigurationFile(string file, out ConfigurationFile configurationFile) {
            configurationFile = null;
            string fileText;

            try {
                fileText = File.ReadAllText(file);
            } catch (IOException ex) {
                AnsiConsole.WriteException(ex);
                return false;
            }

            var tomlOptions = new TomlModelOptions { };
            if (!Toml.TryToModel(fileText, out configurationFile, out var diag, file, tomlOptions)) {
                AnsiConsole.MarkupLine("[red]Error reading Config.toml file.[/]");
                foreach (var item in diag) {
                    if (item.Kind != Tomlyn.Syntax.DiagnosticMessageKind.Error) {
                        continue;
                    }
                    AnsiConsole.MarkupLineInterpolated($"[red]{item.Message}[/]");
                }
                return false;
            }

            if (string.IsNullOrWhiteSpace(configurationFile.ConnectionString)) {
                AnsiConsole.MarkupLine("[red]Config.toml/connection_string cannot be null.[/]");
                return false;
            }

            return true;
        }
    }
}
