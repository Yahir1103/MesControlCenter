namespace MesControlCenter.Core.Services;

public static class PythonDetector
{
    public static string Detect()
    {
        // 1. Try 'py' launcher (Windows Python Launcher)
        var pyPath = FindOnPath("py.exe");
        if (pyPath != null) return pyPath;

        // 2. Try 'python' (skip WindowsApps stub)
        var pythonPath = FindOnPath("python.exe");
        if (pythonPath != null && !pythonPath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            return pythonPath;

        // 3. Fallback
        return "python";
    }

    public static (string Program, List<string> ExtraArgs) SplitInterpreter(string interpreter)
    {
        if (string.IsNullOrWhiteSpace(interpreter))
            return (Detect(), new List<string>());

        var parts = interpreter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return (Detect(), new List<string>());

        return (parts[0], parts.Skip(1).ToList());
    }

    private static string? FindOnPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(fullPath)) return fullPath;
        }

        return null;
    }
}
