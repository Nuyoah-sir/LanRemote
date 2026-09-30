using System.IO;
using System.Runtime.ExceptionServices;

namespace LanRemote.Capture;

/// <summary>数组支持的不可扩展 JPEG 输出；首个底层失败锁存到 Save 结束，后续成功写入不能清除。</summary>
internal sealed class JpegOutputStream : MemoryStream
{
    internal JpegOutputStream(byte[] buffer, int limit)
        : base(buffer, 0, limit, writable: true, publiclyVisible: false)
    {
        base.SetLength(0);
    }

    internal Exception? Failure { get; private set; }

    internal void ThrowIfFailed()
    {
        if (Failure is { } failure) ExceptionDispatchInfo.Throw(failure);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        try { base.Write(buffer, offset, count); }
        catch (Exception error)
        {
            Failure ??= error;
            throw;
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try { base.Write(buffer); }
        catch (Exception error)
        {
            Failure ??= error;
            throw;
        }
    }

    public override void WriteByte(byte value)
    {
        try { base.WriteByte(value); }
        catch (Exception error)
        {
            Failure ??= error;
            throw;
        }
    }

    public override void SetLength(long value)
    {
        try { base.SetLength(value); }
        catch (Exception error)
        {
            Failure ??= error;
            throw;
        }
    }

    public override long Seek(long offset, SeekOrigin loc)
    {
        try { return base.Seek(offset, loc); }
        catch (Exception error)
        {
            Failure ??= error;
            throw;
        }
    }

    public override long Position
    {
        get => base.Position;
        set
        {
            try { base.Position = value; }
            catch (Exception error)
            {
                Failure ??= error;
                throw;
            }
        }
    }

    public override int Capacity
    {
        get => base.Capacity;
        set
        {
            try { base.Capacity = value; }
            catch (Exception error)
            {
                Failure ??= error;
                throw;
            }
        }
    }
}
