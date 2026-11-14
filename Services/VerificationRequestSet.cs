namespace Hanime1Downloader.CSharp.Services;

/// <summary>Concurrent callers share one verification window, but every failed request must recover.</summary>
public sealed class VerificationRequestSet
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _requests = new(StringComparer.Ordinal);
    private long _version;
    private long _confirmedVersion = -1;

    public bool IsConfirmed
    {
        get { lock (_gate) return _requests.Count > 0 && _confirmedVersion == _version; }
    }

    public void Clear()
    {
        lock (_gate) { _requests.Clear(); _version++; }
    }

    public void Add(string? requestUrl)
    {
        lock (_gate)
        {
            var key = requestUrl ?? string.Empty;
            if (_requests.TryGetValue(key, out var count)) _requests[key] = count + 1;
            else { _requests.Add(key, 1); _version++; }
        }
    }

    public void Remove(string? requestUrl)
    {
        lock (_gate)
        {
            var key = requestUrl ?? string.Empty;
            if (!_requests.TryGetValue(key, out var count)) return;
            if (count > 1) _requests[key] = count - 1;
            else { _requests.Remove(key); _version++; }
        }
    }

    public async Task<bool> ConfirmAsync(Func<string, CancellationToken, Task<bool>> probe, CancellationToken cancellationToken)
    {
        string[] requests;
        long version;
        lock (_gate) { requests = _requests.Keys.ToArray(); version = _version; _confirmedVersion = -1; }
        if (requests.Length == 0) return false;
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recovered = await probe(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!recovered) return false;
        }
        lock (_gate)
        {
            if (_version != version) return false;
            _confirmedVersion = version;
            return true;
        }
    }
}
