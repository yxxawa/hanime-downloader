using Hanime1Downloader.CSharp.Services;

namespace Hanime1Downloader.CSharp.Views;

public partial class CloudflareWindow
{
    private Func<CancellationToken, Task<bool>>? _confirmRecoveryAsync;
    private Func<bool>? _isRecoveryConfirmed;
    private CancellationTokenSource? _verificationCancellation;
    private DateTime _nextRecoveryCheckAt;
    private static readonly TimeSpan RecoveryCheckInterval = TimeSpan.FromSeconds(3);

    /// <summary>Retry the exact failed GET without caches. Non-HTML bodies are canceled, not downloaded.</summary>
    public async Task<bool> ProbeVerificationRequestAsync(string? requestUrl, CancellationToken cancellationToken = default)
    {
        var target = VerificationRequestPolicy.ResolveTarget(_sessionContext, requestUrl);
        if (target is null) return false;
        var response = await TryFetchHtmlInPageAsync(target.PathAndQuery, cancellationToken, verifyRequest: true);
        return VerificationRequestPolicy.IsAccepted(response, target);
    }
}
