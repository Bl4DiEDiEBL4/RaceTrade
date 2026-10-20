using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using RaceTrader;

/// <summary>Shares lookup results only for the lifetime of one release evaluation.</summary>
internal sealed class ReleaseMetadataLookup
{
    private readonly Lazy<Task<MovieReleaseInfo>> _movie;
    private readonly ConcurrentDictionary<int, Lazy<Task<TVShowReleaseInfo>>> _shows = new();
    private readonly Func<int, Task<TVShowReleaseInfo>> _loadShow;

    public ReleaseMetadataLookup(string releaseName)
        : this(() => IMDBHelper.EnrichReleaseInfo(releaseName),
            days => TVMazeHelper.EnrichReleaseInfo(releaseName, days))
    {
    }

    internal ReleaseMetadataLookup(
        Func<Task<MovieReleaseInfo>> loadMovie,
        Func<int, Task<TVShowReleaseInfo>> loadShow)
    {
        _movie = new Lazy<Task<MovieReleaseInfo>>(loadMovie, LazyThreadSafetyMode.ExecutionAndPublication);
        _loadShow = loadShow;
    }

    public Task<MovieReleaseInfo> GetMovieAsync() => _movie.Value;

    public Task<TVShowReleaseInfo> GetShowAsync(int cacheDays) =>
        _shows.GetOrAdd(cacheDays, days => new Lazy<Task<TVShowReleaseInfo>>(
            () => _loadShow(days), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
}
