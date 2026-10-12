using System.IO;

namespace Orynivo;

/// <summary>
/// Centralises application data paths and one-time migration from the legacy
/// <c>Player</c> directory. Explicit data roots remain isolated from legacy data.
/// </summary>
public static class AppPaths
{
    /// <summary>Current product name used as the local application data subdirectory.</summary>
    public const string ProductName = "Orynivo";

    /// <summary>Legacy product name whose data is migrated on first launch.</summary>
    public const string LegacyProductName = "Player";

    /// <summary>
    /// Environment variable selecting an isolated data directory for desktop tests
    /// or service-owned server storage. Explicit roots never import legacy data.
    /// </summary>
    public const string DataDirEnvironmentVariable = "ORYNIVO_DATA_DIR";

    /// <summary>Captures the override together with the process-wide data root.</summary>
    private static readonly string? DataRootOverride = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);

    /// <summary>
    /// Root data directory, defaulting to the local application data <c>Orynivo</c>
    /// directory unless <see cref="DataDirEnvironmentVariable"/> selects another root.
    /// </summary>
    public static string DataRoot { get; } = ResolveDataRoot();

    /// <summary>Resolves the data root using the captured process override.</summary>
    /// <returns>The explicit root or the default product directory.</returns>
    private static string ResolveDataRoot()
    {
        if (!string.IsNullOrWhiteSpace(DataRootOverride))
            return DataRootOverride;

        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create),
            ProductName);
    }

    /// <summary>Legacy data directory beneath local application data.</summary>
    public static string LegacyDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create),
        LegacyProductName);

    /// <summary>
    /// Copies missing legacy files once for the default root. Explicit roots only
    /// receive a completion marker and never import settings or caches from Player.
    /// Existing destination files are preserved, including concurrent creations.
    /// </summary>
    public static void MigrateLegacyData()
        => MigrateLegacyData(LegacyDataRoot, DataRoot, !string.IsNullOrWhiteSpace(DataRootOverride));

    /// <summary>Migrates an explicitly supplied root pair without accessing process data.</summary>
    /// <param name="legacyDataRoot">Legacy source directory.</param>
    /// <param name="dataRoot">Destination directory.</param>
    /// <param name="isExplicitRoot">Whether the destination must remain isolated.</param>
    internal static void MigrateLegacyData(string legacyDataRoot, string dataRoot, bool isExplicitRoot)
    {
        var migrationMarker = Path.Combine(dataRoot, ".legacy-data-migrated");
        if (File.Exists(migrationMarker))
            return;

        Directory.CreateDirectory(dataRoot);
        if (!isExplicitRoot && Directory.Exists(legacyDataRoot))
            CopyMissingFiles(legacyDataRoot, dataRoot);

        File.WriteAllText(migrationMarker, DateTimeOffset.UtcNow.ToString("O"));
    }

    /// <summary>Builds a path beneath the process data root.</summary>
    /// <param name="parts">Path segments to append to the data root.</param>
    /// <returns>The combined path.</returns>
    public static string GetDataPath(params string[] parts)
    {
        var path = DataRoot;
        foreach (var part in parts)
            path = Path.Combine(path, part);
        return path;
    }

    /// <summary>Copies a legacy directory tree without replacing destination files.</summary>
    /// <param name="sourceDirectory">Legacy source directory.</param>
    /// <param name="targetDirectory">Destination directory.</param>
    private static void CopyMissingFiles(string sourceDirectory, string targetDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var targetFile = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            if (!File.Exists(targetFile))
                CopyMissingFile(file, targetFile);
        }
    }

    /// <summary>
    /// Copies one missing file, preserving a destination created after the caller's
    /// existence check. Other I/O failures propagate and prevent completion marking.
    /// </summary>
    /// <param name="sourceFile">Legacy source file.</param>
    /// <param name="targetFile">Destination file that must never be overwritten.</param>
    internal static void CopyMissingFile(string sourceFile, string targetFile)
    {
        try
        {
            File.Copy(sourceFile, targetFile, overwrite: false);
        }
        catch (IOException exception) when (
            (exception.HResult & 0xffff) is 17 or 80 or 183 && File.Exists(targetFile))
        {
            // EEXIST / ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS: another writer won.
        }
    }
}
