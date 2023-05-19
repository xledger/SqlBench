# Sql Bench

[![NuGet version (Xledger.Sql)](https://img.shields.io/nuget/v/SqlBench.svg?style=flat-square)](https://www.nuget.org/packages/SqlBench/)

A console tool for benchmarking sql.

# Installation

```powershell
dotnet tool install --global SqlBench --version 1.0.1
```

# Usage

```powershell
mkdir sql_benchmarks
cd sql_benchmarks
sqlbench init

# Edit Config.toml, set your connection_string
# Edit BenchCases.toml, add SQL you want to test

sqlbench bench BenchCases.toml
```
