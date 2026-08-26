namespace LiteDocumentStore;

/// <summary>
/// One caller's claim on the store's bound for concurrently open blob read streams, released
/// when the stream it belongs to is disposed.
/// </summary>
/// <remarks>
/// <para>
/// A blob read stream is handed to the caller and holds its connection until they dispose it,
/// so the connections cannot be rented from the operation pool — one forgetful caller would
/// starve every other operation in the store. They are opened outside it instead, which leaves
/// nothing bounding how many a caller can open at once. This is that bound: separate from
/// <see cref="DocumentStoreOptions.MaxPoolSize"/> so the two cannot starve each other, and equal
/// to it so there is only one number to reason about.
/// </para>
/// <para>
/// <see cref="Release"/> is idempotent, because the release paths overlap deliberately: the open
/// path releases on failure and the caller's <c>catch</c> may release again, and a stream that is
/// finalized rather than disposed releases from the finalizer.
/// </para>
/// </remarks>
internal sealed class BlobStreamSlot(SqliteConnectionPool pool)
{
    private int _released;

    /// <summary>
    /// Returns the slot, at most once however many times this is called.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        pool.ReleaseBlobStreamSlot();
    }
}
