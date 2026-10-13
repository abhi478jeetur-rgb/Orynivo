using System.Text.Json;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Checks atomic optional restart storage without windows or personal data.</summary>
public sealed class ViewPreviewStoreTests
{
    /// <summary>Checks restart readback, profile isolation and filename privacy.</summary>
    /// <returns>A task representing isolated file verification.</returns>
    [Fact]
    public async Task Restart_ReadsOnlyMatchingContextAndSection()
    {
        var root = Path.Combine(Path.GetTempPath(), "orynivo-preview-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ViewPreviewStore(root);
            await store.WriteAsync("synthetic-profile", "cloud;rock", new Sample(3, "Safe display"), () => true);
            var restarted = new ViewPreviewStore(root);
            Assert.Equal(new Sample(3, "Safe display"), await restarted.ReadAsync<Sample>("synthetic-profile", "cloud;rock"));
            Assert.Null(await restarted.ReadAsync<Sample>("other-profile", "cloud;rock"));
            Assert.Null(await restarted.ReadAsync<Sample>("synthetic-profile", "cloud;jazz"));
            Assert.DoesNotContain("synthetic-profile", Directory.GetFiles(Path.Combine(root, "view-previews-v1")).Single());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>Checks rejected configuration ownership preserves the previous file.</summary>
    /// <returns>A task representing deterministic write admission.</returns>
    [Fact]
    public async Task ConfigurationSupersession_DoesNotReplacePreview()
    {
        var root = Path.Combine(Path.GetTempPath(), "orynivo-preview-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ViewPreviewStore(root);
            await store.WriteAsync("context", "summary", new Sample(3, "Old"), () => true);
            var checks = 0;
            await store.WriteAsync("context", "summary", new Sample(9, "Rejected"), () => ++checks == 1);
            Assert.Equal(2, checks);
            Assert.Equal(new Sample(3, "Old"), await store.ReadAsync<Sample>("context", "summary"));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "view-previews-v1")));
            await store.ClearAsync();
            Assert.Null(await store.ReadAsync<Sample>("context", "summary"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>Checks corrupt, incomplete, oversized and expired previews are optional cache misses.</summary>
    /// <returns>A task representing isolated corrupt-cache recovery.</returns>
    [Fact]
    public async Task InvalidPreviews_AreCacheMissesAndPermitRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "orynivo-preview-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ViewPreviewStore(root);
            await store.WriteAsync("context", "section", new Sample(3, "Safe"), () => true);
            var path = Directory.GetFiles(Path.Combine(root, "view-previews-v1")).Single();
            foreach (var invalid in new[] { "broken", "{}", "{\"Count\":3,\"Label\":null}" })
            {
                await File.WriteAllTextAsync(path, invalid);
                Assert.Null(await store.ReadAsync<Sample>("context", "section"));
            }
            await File.WriteAllTextAsync(path, new string(' ', 4 * 1024 * 1024 + 1));
            Assert.Null(await store.ReadAsync<Sample>("context", "section"));
            await store.WriteAsync("context", "section", new Sample(7, "Recovered"), () => true);
            Assert.Equal(new Sample(7, "Recovered"), await store.ReadAsync<Sample>("context", "section"));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
            Assert.Null(await store.ReadAsync<Sample>("context", "section"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>Checks the file-count bound across many view/options identities.</summary>
    /// <returns>A task representing bounded eviction.</returns>
    [Fact]
    public async Task ManySections_StayWithinPreviewBound()
    {
        var root = Path.Combine(Path.GetTempPath(), "orynivo-preview-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ViewPreviewStore(root);
            for (var index = 0; index < 70; index++)
                await store.WriteAsync("context", index.ToString(), new Sample(index, "Safe"), () => true);
            Assert.Equal(64, Directory.GetFiles(Path.Combine(root, "view-previews-v1"), "*.json").Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>Immutable synthetic display payload.</summary>
    /// <param name="Count">Synthetic counter.</param>
    /// <param name="Label">Non-sensitive display text.</param>
    private sealed record Sample(int Count, string Label);
}
