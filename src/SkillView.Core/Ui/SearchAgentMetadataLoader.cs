using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Threading;

namespace SkillView.Ui;

/// <summary>
/// Loads the preview front matter needed by Discover's agent filter without
/// allowing one search to fan out into an unbounded number of subprocesses.
/// </summary>
internal sealed class SearchAgentMetadataLoader
{
    internal const int DefaultMaxConcurrency = 4;
    internal static readonly TimeSpan DefaultPreviewTimeout = TimeSpan.FromSeconds(15);

    private readonly SearchAgentMetadataCache _cache;
    private readonly Logger _logger;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _previewTimeout;
    private readonly SemaphoreSlim _previewSlots;

    internal SearchAgentMetadataLoader(
        SearchAgentMetadataCache cache,
        Logger logger,
        int maxConcurrency = DefaultMaxConcurrency,
        TimeSpan? previewTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        var effectiveTimeout = previewTimeout ?? DefaultPreviewTimeout;
        if (!CancellationSource.IsSupportedTimeout(effectiveTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(previewTimeout));
        }

        _cache = cache;
        _logger = logger;
        _maxConcurrency = maxConcurrency;
        _previewTimeout = effectiveTimeout;
        _previewSlots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    internal async Task<IReadOnlyList<SearchResultSkill>> FilterAsync(
        IReadOnlyList<SearchResultSkill> results,
        string? requestedAgent,
        Func<SearchResultSkill, CancellationToken, Task<PreviewResult>> loadPreviewAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(loadPreviewAsync);

        var normalizedAgent = SearchAgentMetadataCache.NormalizeAgent(requestedAgent);
        if (normalizedAgent is null)
        {
            return results;
        }

        await Parallel.ForEachAsync(
            results,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _maxConcurrency,
            },
            async (result, token) =>
            {
                await EnsureLoadedAsync(result, loadPreviewAsync, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return _cache.Filter(results, normalizedAgent);
    }

    private async Task EnsureLoadedAsync(
        SearchResultSkill result,
        Func<SearchResultSkill, CancellationToken, Task<PreviewResult>> loadPreviewAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cache.Has(result))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(result.Repo))
        {
            _cache.Store(result, ImmutableArray<string>.Empty);
            return;
        }

        await _previewSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another overlapping search may have populated this result while
            // this worker waited for a globally bounded preview slot.
            if (_cache.Has(result))
            {
                return;
            }

            using var timeout = new CancellationSource(
                cancellationToken,
                _previewTimeout,
                CancellationCallbackReporter.For(
                    _logger,
                    "metadata preview",
                    "search.agent"));
            try
            {
                var preview = await loadPreviewAsync(result, timeout.Token).ConfigureAwait(false);
                if (!preview.Succeeded)
                {
                    // A subprocess failure is transient. Leave it uncached so
                    // a later search can retry instead of persisting a false
                    // "agent not present" result for the cache lifetime.
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var agents = SearchAgentMetadataCache.ExtractAgentsFromMarkdown(
                    preview.MarkdownBody ?? preview.Body ?? string.Empty);
                _cache.Store(result, agents);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                _logger.Warn(
                    "search.agent",
                    $"{result.Repo}/{result.SkillName}: metadata preview timed out after {_previewTimeout.TotalSeconds:F0}s");
            }
            catch (Exception ex)
            {
                _logger.Warn("search.agent", $"{result.Repo}/{result.SkillName}: {ex.Message}");
            }
        }
        finally
        {
            _previewSlots.Release();
        }
    }
}
