using Xunit;

namespace Orynivo.Core.Tests;

/// <summary>Tests legacy migration using temporary directories only.</summary>
public sealed class AppPathsTests : IDisposable
{
    /// <summary>Owns the isolated fixture directories.</summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orynivo-migration-tests-{Guid.NewGuid():N}");

    /// <summary>Copies missing nested files, preserves current settings and completes once.</summary>
    [Fact]
    public void MigrateLegacyData_PreservesExistingFilesAndSkipsCompletedMigration()
    {
        var legacy = Path.Combine(_root, "legacy");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(Path.Combine(legacy, "artworks", "original"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy");
        File.WriteAllText(Path.Combine(target, "settings.json"), "current");
        var artwork = Path.Combine("artworks", "original", "cover.jpg");
        File.WriteAllText(Path.Combine(legacy, artwork), "artwork");

        AppPaths.MigrateLegacyData(legacy, target, isExplicitRoot: false);

        Assert.Equal("current", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal("artwork", File.ReadAllText(Path.Combine(target, artwork)));
        Assert.True(File.Exists(Path.Combine(target, ".legacy-data-migrated")));
        File.WriteAllText(Path.Combine(legacy, "later.txt"), "later");
        AppPaths.MigrateLegacyData(legacy, target, isExplicitRoot: false);
        Assert.False(File.Exists(Path.Combine(target, "later.txt")));
    }

    /// <summary>An explicit data root never imports legacy configuration or artwork.</summary>
    [Fact]
    public void MigrateLegacyData_ExplicitRootRemainsEmptyExceptMarker()
    {
        var legacy = Path.Combine(_root, "legacy");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy");

        AppPaths.MigrateLegacyData(legacy, target, isExplicitRoot: true);

        Assert.Equal(new[] { ".legacy-data-migrated" }, Directory.GetFiles(target).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(target));
    }

    /// <summary>A first launch without legacy storage still creates the marker.</summary>
    [Fact]
    public void MigrateLegacyData_AbsentLegacyCreatesMarker()
    {
        var target = Path.Combine(_root, "target");
        AppPaths.MigrateLegacyData(Path.Combine(_root, "absent"), target, isExplicitRoot: false);
        Assert.True(File.Exists(Path.Combine(target, ".legacy-data-migrated")));
    }

    /// <summary>Deterministically exercises a destination appearing before the copy.</summary>
    [Fact]
    public void CopyMissingFile_ConcurrentDestinationIsPreserved()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.jpg");
        var target = Path.Combine(_root, "target.jpg");
        File.WriteAllText(source, "legacy artwork");
        File.WriteAllText(target, "new artwork");

        AppPaths.CopyMissingFile(source, target);

        Assert.Equal("new artwork", File.ReadAllText(target));
    }

    /// <summary>Missing source errors are never mistaken for a destination race.</summary>
    [Fact]
    public void CopyMissingFile_MissingSourcePropagates()
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<FileNotFoundException>(() => AppPaths.CopyMissingFile(
            Path.Combine(_root, "absent.jpg"), Path.Combine(_root, "target.jpg")));
    }

    /// <summary>A failed tree copy never writes a successful completion marker.</summary>
    [Fact]
    public void MigrateLegacyData_CopyFailureLeavesMigrationRetryable()
    {
        var legacy = Path.Combine(_root, "legacy");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(Path.Combine(target, "settings.json"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy");

        var failure = Record.Exception(() => AppPaths.MigrateLegacyData(legacy, target, isExplicitRoot: false));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.False(File.Exists(Path.Combine(target, ".legacy-data-migrated")));
    }

    /// <summary>Removes only the fixture-owned temporary directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
