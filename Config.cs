namespace SqlBench {
    public static class Config {
        public static string GetDataDir() {
            var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directory = Path.Combine(localAppDataPath, "SqlBench");
            Directory.CreateDirectory(directory);
            return Path.GetFullPath(directory);
        }
    }
}
