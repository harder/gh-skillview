using SkillView.Gh;
using SkillView.Threading;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{
    private GhAuthStatus? _remoteAuth;
    private long _discoverAuthGeneration = -1;
    private Func<CancellationToken, Task<GhAuthStatus>>? _authProbeForTests;
    private async Task<GhAuthStatus> ProbeAuthenticationAsync(string path, CancellationToken token)
    {
        using var deadline = new CancellationSource(token, TimeSpan.FromSeconds(30),
            CancellationCallbackReporter.For(_services.Logger, "authentication"));
        try
        {
            var auth = await (_authProbeForTests is { } probe ? probe(deadline.Token)
                : _services.AuthService.GetStatusAsync(path, deadline.Token)).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return auth;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException("gh auth status timed out after 30 seconds; local browsing remains available. Retry Doctor or check gh authentication.");
        }
    }

    // Optional remote context belongs to the explicit search activation. Neither
    // its wait nor its failure is part of local inventory admission.
    private void StartDiscoverAuthentication()
    {
        var generation = Interlocked.Read(ref _discoverGeneration);
        var path = _ghPath;
        if (path is null || _discoverAuthGeneration == generation
            || !TryCaptureActiveLifetimeToken(_discoverLifetime, out var token)) return;
        _discoverAuthGeneration = generation;
        RunOwnedTask(async () =>
        {
            try
            {
                var auth = await ProbeAuthenticationAsync(path, token).ConfigureAwait(false);
                await InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !IsDiscoverWorkspaceActive(generation)) return;
                    _remoteAuth = auth;
                    UpdateMetadataPane();
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { _services.Logger.Error("discover.auth", ex.ToString()); }
        }, "discover.auth");
    }
}
