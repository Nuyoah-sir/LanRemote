namespace LanRemote.Transport;

internal enum HostLifecycleErrorKind
{
    Handler,
    Connection,
    SocketCleanup,
    StreamCleanup,
    CloseCleanup,
    AdmissionCleanup,
    RegistrationCleanup,
}

internal sealed record HostLifecycleError(HostLifecycleErrorKind Kind, Exception Error);

/// <summary>每个固定类别只保留首个异常；整个 Host 最多保留七项，不随连接数增长。</summary>
internal sealed class HostLifecycleErrors
{
    private readonly Exception?[] _first = new Exception?[7];

    internal void Record(HostLifecycleErrorKind kind, Exception error)
    {
        Interlocked.CompareExchange(ref _first[(int)kind], error, null);
    }

    internal IReadOnlyList<HostLifecycleError> Snapshot
    {
        get
        {
            List<HostLifecycleError> result = new(_first.Length);
            for (int i = 0; i < _first.Length; i++)
            {
                if (Volatile.Read(ref _first[i]) is { } error)
                {
                    result.Add(new HostLifecycleError((HostLifecycleErrorKind)i, error));
                }
            }

            return result.AsReadOnly();
        }
    }
}
