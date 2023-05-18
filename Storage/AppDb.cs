using SQLite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SqlBench.Storage {
    internal static class AppDb {
        public static SQLiteConnection GetConnection() {
            var dataDir = Config.GetDataDir();

            var dbPath = Path.Combine(dataDir, "SqlBench.db");
            var db = new SQLiteConnection(dbPath);
            db.CreateTable<Setting>();
            db.CreateTable<BenchmarkRun>();
            return db;
        }

        public static string FetchSetting(string key) {
            using var db = GetConnection();
            return db.Table<Setting>().Where(s => s.setting_key == key).FirstOrDefault()?.setting_value;
        }

        public static void UpsertSetting(string key, string value) {
            using var db = GetConnection();
            db.Execute(@"
insert into Setting(setting_key, setting_value)
values (?, ?)
on conflict(setting_key) do update set
    setting_value = excluded.setting_value;", key, value);
        }
    }

    public class Setting {
        [PrimaryKey, AutoIncrement] public int id { get; set; }
        [Indexed(Unique = true)] public string setting_key { get; set; }
        [MaxLength(int.MaxValue)] public string setting_value { get; set; }
    }

    public class BenchmarkRun {
        [PrimaryKey, AutoIncrement] public int id { get; set; }
        [MaxLength(int.MaxValue)] public string Name { get; set; }
        [MaxLength(int.MaxValue)] public string ConnectionString { get; set; }
        [MaxLength(int.MaxValue)] public string Sql { get; set; }
        [MaxLength(int.MaxValue)] public string ResultsJson { get; set; }
        public DateTime RunAt { get; set; }
    }
}
