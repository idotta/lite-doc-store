using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LiteDocumentStore;

/// <summary>
/// A read-only <see cref="Stream"/> over one row of the blob table, backed by SQLite's
/// incremental blob I/O, that owns the connection and read transaction it reads through.
/// </summary>
/// <remarks>
/// <para>
/// The connection is opened <em>outside</em> the store's pool, on purpose. A stream is handed
/// to the caller and lives until they dispose it, so a caller who forgets would otherwise hold
/// a pooled connection permanently and, after <see cref="DocumentStoreOptions.MaxPoolSize"/>
/// such leaks, starve every other operation. On its own connection a leak costs one handle,
/// affects nothing else, and is reclaimed when the provider's own finalizers run.
/// </para>
/// <para>
/// The read transaction is what makes the row's rowid stable: without it, another connection
/// could delete the row between the rowid lookup and the blob open, and SQLite may reuse that
/// rowid for a different row. It is deferred, so it takes no lock and blocks no writer.
/// </para>
/// <para>
/// Disposal order is blob, then transaction, then connection — each in a <c>finally</c>, so a
/// failure part-way still releases the rest.
/// </para>
/// </remarks>
internal sealed class BlobReadStream : Stream
{
    private readonly string _id;
    private readonly ILogger _logger;
    private SqliteBlob? _blob;
    private SqliteTransaction? _transaction;
    private SqliteConnection? _connection;
    private int _disposed;

    internal BlobReadStream(
        SqliteBlob blob,
        SqliteTransaction transaction,
        SqliteConnection connection,
        string id,
        ILogger logger)
    {
        _blob = blob;
        _transaction = transaction;
        _connection = connection;
        _id = id;
        _logger = logger;
    }


    /// <summary>
    /// Opens a read stream over one blob on a connection this stream takes ownership of,
    /// returning null when the id is absent. The connection is disposed on every failure path,
    /// so a caller that gets null or an exception owns nothing.
    /// </summary>
    internal static async Task<BlobReadStream?> OpenAsync(
        SqliteConnection connection,
        string id,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        SqliteTransaction? transaction = null;

        try
        {
            // Deferred, so it takes no lock: its job is to pin a read snapshot, which is what
            // keeps the rowid resolved below from being reused by another connection before the
            // blob handle opens.
            transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: true);

            var rowId = await connection.QueryFirstInt64Async(
                SqlGenerator.GenerateBlobRowIdSql(), cancellationToken, ("Id", id)).ConfigureAwait(false);

            if (rowId is null)
            {
                logger.LogDebug("Blob {Id} not found", id);
                await transaction.DisposeAsync().ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            var blob = new SqliteBlob(
                connection, SqlGenerator.BlobTableName, "data", rowId.Value, readOnly: true);

            return new BlobReadStream(blob, transaction, connection, id, logger);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => Blob().Length;

    /// <inheritdoc />
    public override long Position
    {
        get => Blob().Position;
        set => Blob().Position = value;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Blob().Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => Blob().Read(buffer);

    /// <inheritdoc />
    public override int ReadByte() => Blob().ReadByte();

    // SQLite's incremental blob I/O is synchronous, so the async members complete inline rather
    // than pretending to be non-blocking. Overriding them anyway keeps Stream's defaults — which
    // would allocate a task and copy through a rented buffer — out of the path.
    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken);
        }

        try
        {
            return Task.FromResult(Blob().Read(buffer, offset, count));
        }
        catch (Exception ex)
        {
            return Task.FromException<int>(ex);
        }
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        try
        {
            return ValueTask.FromResult(Blob().Read(buffer.Span));
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<int>(ex);
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => Blob().Seek(offset, origin);

    /// <inheritdoc />
    public override void Flush()
    {
        // Read-only: nothing is buffered.
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("A blob read stream is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A blob read stream is read-only.");

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("A blob read stream is read-only.");

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        GC.SuppressFinalize(this);

        var blob = Interlocked.Exchange(ref _blob, null);
        var transaction = Interlocked.Exchange(ref _transaction, null);
        var connection = Interlocked.Exchange(ref _connection, null);

        try
        {
            await SafeDisposeAsync(blob, "blob handle").ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await SafeDisposeAsync(transaction, "read transaction").ConfigureAwait(false);
            }
            finally
            {
                await SafeDisposeAsync(connection, "blob stream connection").ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // The finalizer must not touch the provider's objects — they have their own finalizers,
        // and the SafeHandle behind the connection closes the database whether or not this runs.
        // All it does is make a leak visible instead of silent.
        if (!disposing)
        {
            _logger.LogError(
                "Blob read stream for {Id} was never disposed; its connection stayed open until finalization",
                _id);
            base.Dispose(disposing);
            return;
        }

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var blob = Interlocked.Exchange(ref _blob, null);
        var transaction = Interlocked.Exchange(ref _transaction, null);
        var connection = Interlocked.Exchange(ref _connection, null);

        try
        {
            SafeDispose(blob, "blob handle");
        }
        finally
        {
            try
            {
                SafeDispose(transaction, "read transaction");
            }
            finally
            {
                SafeDispose(connection, "blob stream connection");
            }
        }

        base.Dispose(disposing);
    }

    ~BlobReadStream() => Dispose(disposing: false);

    private SqliteBlob Blob()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _blob!;
    }

    private void SafeDispose(IDisposable? resource, string what)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose the {What} of a blob read stream", what);
        }
    }

    private async ValueTask SafeDisposeAsync(IAsyncDisposable? resource, string what)
    {
        try
        {
            if (resource is not null)
            {
                await resource.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose the {What} of a blob read stream", what);
        }
    }
}
