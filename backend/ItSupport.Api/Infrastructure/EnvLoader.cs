namespace ItSupport.Api.Infrastructure;

public static class EnvLoader
{
    public static void AddLocalEnv(ConfigurationManager configuration, string contentRoot)
    {
        var path = Path.GetFullPath(Path.Combine(contentRoot, "..", "..", ".env"));
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            var split = line.IndexOf('='); if (split < 1) continue;
            var key = line[..split].Trim().Replace("__", ":"); var value = line[(split + 1)..].Trim().Trim('"', '\'');
            if (OperatingSystem.IsWindows() && key == "CONNECTIONSTRINGS:DEFAULT" && value.Contains("/app/", StringComparison.OrdinalIgnoreCase)) continue;
            configuration[key] = value;
        }
    }
}
