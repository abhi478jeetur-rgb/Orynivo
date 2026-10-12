using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orynivo.Library;

namespace CatalogBench;

/// <summary>Measures production database readers against generated metadata without a personal catalog or UI.</summary>
internal static class Program
{
    /// <summary>Runs explicit warm-query measurements and saves path-free JSON.</summary>
    /// <param name="args">One output JSON path.</param>
    /// <returns>Zero after successful measurement.</returns>
    private static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Specify one output JSON path.");
        var root = Path.Combine(Path.GetTempPath(), "orynivo-catalog-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousProfile = AudioDatabase.ActiveProfileId;
        var results = new List<QueryMeasurement>();
        try
        {
            AudioDatabase.SetActiveProfile("benchmark");
            foreach (var size in new[] { 1000, 100000 })
            {
                var path = Path.Combine(root, size + ".db");
                Seed(path, size);
                using var db = new AudioDatabase(path);
                Measure(size, "first-page-500", () => db.GetTrackListPage(0, 500).Count, 500, results);
                Measure(size, "last-page-500", () => db.GetTrackListPage((size - 1) / 500, 500).Count, 500, results);
                Measure(size, "ids-500", () => db.GetTrackListByIds(Enumerable.Range(1, 500).Select(id => (long)id)).Count, 500, results);
                Measure(size, "all-compact-tracks", () => db.GetTrackList().Count, size, results);
                Measure(size, "all-track-facets", () => db.GetTrackFacets().Count, size, results);
                Measure(size, "all-albums", () => db.GetAlbumsLite().Count, size / 10, results);
                Measure(size, "all-artists", () => db.GetArtistsLite().Count, size / 100, results);
                Measure(size, "dashboard-summary", () =>
                {
                    var summary = db.GetDashboardLibrarySummary();
                    if (summary.AlbumCount != size / 10 || summary.ArtistCount != size / 100 || summary.FavoriteCount != size / 10)
                        throw new InvalidOperationException("Synthetic summary counts differ.");
                    return summary.TrackCount;
                }, size, results);
                Console.WriteLine($"Measured {size} synthetic tracks.");
            }
            var report = new { SchemaVersion = 1, Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount, Samples = 7, WarmupCallsPerQuery = 1,
                Scope = "Warm synchronous SQLite readers after bulk seeding; not cold cache, UI, HTTP, scanning, audio or whole-process memory.",
                Measurements = results };
            var output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally
        {
            AudioDatabase.SetActiveProfile(previousProfile);
            foreach (var path in Directory.EnumerateFiles(root, "*.db"))
            {
                using var connection = new SqliteConnection($"Data Source={path}");
                SqliteConnection.ClearPool(connection);
            }
            var fullRoot = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullRoot).StartsWith("orynivo-catalog-bench-", StringComparison.Ordinal))
                throw new InvalidOperationException("Benchmark cleanup left its owned temporary directory.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    /// <summary>Warms one reader, validates row counts and records elapsed time plus thread allocations.</summary>
    /// <param name="tracks">Generated catalog size.</param><param name="query">Fixed phase name.</param>
    /// <param name="read">Production synchronous reader.</param><param name="expected">Expected result count.</param>
    /// <param name="results">Output measurement collection.</param>
    private static void Measure(int tracks, string query, Func<int> read, int expected, List<QueryMeasurement> results)
    {
        if (read() != expected) throw new InvalidOperationException("Synthetic warmup count differs.");
        var timings = new List<double>(); var allocations = new List<long>();
        for (var sample = 0; sample < 7; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            var count = read(); timer.Stop();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (count != expected) throw new InvalidOperationException("Synthetic measured count differs.");
            timings.Add(timer.Elapsed.TotalMilliseconds); allocations.Add(bytes);
        }
        results.Add(new(tracks, query, expected, timings.Min(), timings.Order().ElementAt(3), timings.Max(),
            allocations.Order().ElementAt(3), timings, allocations));
    }

    /// <summary>Bulk seeds deterministic synthetic metadata into the production schema in one SQLite transaction.</summary>
    /// <param name="path">Owned temporary database path.</param><param name="tracks">Catalog size divisible by 100.</param>
    private static void Seed(string path, int tracks)
    {
        using (var schema = new AudioDatabase(path)) { }
        using var connection = new SqliteConnection($"Data Source={path}"); connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM seq WHERE i<$count/100)
            INSERT INTO artists(id,name) SELECT i,printf('Artist %06d',i) FROM seq;
            WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM seq WHERE i<$count/10)
            INSERT INTO albums(id,title,artist_id) SELECT i,printf('Album %06d',i),(i-1)/10+1 FROM seq;
            WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM seq WHERE i<$count)
            INSERT INTO tracks(id,path,file_name,modified_at,added_at,title,sort_title,artist,album_artist,album,
                artist_id,album_id,genre,format,duration,bitrate,is_favorite,user_rating)
            SELECT i,printf('/synthetic/track-%06d.flac',i),printf('track-%06d.flac',i),1,1,
                printf('Track %06d',i),printf('Track %06d',i),printf('Artist %06d',(i-1)/100+1),
                printf('Artist %06d',(i-1)/100+1),printf('Album %06d',(i-1)/10+1),
                (i-1)/100+1,(i-1)/10+1,CASE WHEN i%2=0 THEN 'Rock' ELSE 'Jazz' END,'flac',240,1000,i%10=0,3 FROM seq;
            INSERT INTO profile_track_state(profile_id,track_id,is_favorite,user_rating)
            SELECT 'benchmark',id,1,4 FROM tracks WHERE id%10=0;
            """;
        command.Parameters.AddWithValue("$count", tracks);
        command.ExecuteNonQuery(); transaction.Commit();
    }
}

/// <summary>Stores timings, allocation samples and validated row counts without source paths or metadata.</summary>
/// <param name="Tracks">Catalog size.</param><param name="Query">Fixed reader name.</param><param name="Rows">Validated result count.</param>
/// <param name="MinMs">Minimum elapsed milliseconds.</param><param name="MedianMs">Median elapsed milliseconds.</param>
/// <param name="MaxMs">Maximum elapsed milliseconds.</param><param name="MedianAllocatedBytes">Median current-thread managed allocation.</param>
/// <param name="SamplesMs">All elapsed samples.</param><param name="SamplesAllocatedBytes">All allocation samples.</param>
internal sealed record QueryMeasurement(int Tracks, string Query, int Rows, double MinMs, double MedianMs,
    double MaxMs, long MedianAllocatedBytes, List<double> SamplesMs, List<long> SamplesAllocatedBytes);