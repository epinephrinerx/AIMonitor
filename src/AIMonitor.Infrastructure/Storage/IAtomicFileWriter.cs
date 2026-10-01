namespace AIMonitor.Infrastructure.Storage;

internal interface IAtomicFileWriter
{
    Task WriteAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken);
}
