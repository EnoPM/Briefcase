using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.ManagedHost;

/// <summary>
/// Owns every RootSet reference acquired by one mod generation. The scope only
/// stores native object handles and framework-owned tokens, so it never keeps a
/// collectible mod assembly alive after unload.
/// </summary>
internal sealed class UnrealAssetScope : IUnrealAssetScope
{
    private readonly object _gate = new();
    private readonly UnrealApi _unreal;
    private readonly string _modId;
    private readonly Action<string> _warning;
    private readonly HashSet<LeaseToken> _leases = [];
    private bool _disposed;

    public UnrealAssetScope(
        UnrealApi unreal,
        string modId,
        Action<string> warning) =>
        (_unreal, _modId, _warning) = (unreal, modId, warning);

    public bool IsAvailable => !_disposed && _unreal.IsAssetLoadingAvailable;
    public UnrealApi Unreal => _unreal;

    public UnrealObjectHandle Load(
        string path,
        string expectedClassPath,
        out IUnrealLeaseLifetime lifetime)
    {
        ThrowIfDisposed();
        var handle = _unreal.LoadObjectHandle(path, expectedClassPath);
        lifetime = Retain(handle);
        return handle;
    }

    public IUnrealLeaseLifetime Retain(UnrealObjectHandle handle)
    {
        ThrowIfDisposed();
        _unreal.AcquireObjectRoot(handle);
        var token = new LeaseToken(this, handle);
        lock (_gate)
        {
            if (_disposed)
            {
                // The scope raced with unload after the native acquire. Release
                // immediately rather than leaving a process-wide root behind.
                token.MarkDisposed();
                TryRelease(handle);
                throw new ObjectDisposedException(nameof(UnrealAssetScope));
            }
            _leases.Add(token);
        }
        return token;
    }

    public void Dispose()
    {
        LeaseToken[] leases;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            leases = _leases.ToArray();
            _leases.Clear();
        }
        foreach (var lease in leases)
            if (lease.MarkDisposed()) TryRelease(lease.Handle);
    }

    private void Release(LeaseToken token)
    {
        lock (_gate) _leases.Remove(token);
        TryRelease(token.Handle);
    }

    private void TryRelease(UnrealObjectHandle handle)
    {
        try { _unreal.ReleaseObjectRoot(handle); }
        catch (Exception exception)
        {
            _warning(
                $"Could not release an Unreal object lease for {_modId}: {exception.Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
            ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class LeaseToken(
        UnrealAssetScope owner,
        UnrealObjectHandle handle) : IUnrealLeaseLifetime
    {
        private int _disposed;
        public UnrealObjectHandle Handle { get; } = handle;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (!MarkDisposed()) return;
            owner.Release(this);
        }

        public bool MarkDisposed() => Interlocked.Exchange(ref _disposed, 1) == 0;
    }
}
