namespace DeviceControl.Api;

/// <summary>Explicitly validated, local-only storage scopes. Never trust a caller-supplied file path.</summary>
public static class StationStoragePaths
{
    public static string Folder(IHostEnvironment environment, string? scope = null)
    {
        var baseFolder = Path.Combine(environment.ContentRootPath, "data");
        if (scope is null) { Directory.CreateDirectory(baseFolder); return baseFolder; }
        if (scope.Length is < 1 or > 32 || !System.Text.RegularExpressions.Regex.IsMatch(scope, @"\A[A-Za-z0-9_-]+\z"))
            throw new ArgumentException("Invalid station storage scope", nameof(scope));
        var folder = Path.Combine(baseFolder, "stations", scope);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
