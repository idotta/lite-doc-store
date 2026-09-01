using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for <see cref="DocumentStoreOptions.Validate"/> and the guards it delegates to.
/// </summary>
/// <remarks>
/// The DI registration hands a raw <see cref="DocumentStoreOptions"/> to the factory, so options
/// built by hand never pass through <see cref="DocumentStoreOptionsBuilder"/>. Validate is the one
/// place that rejects an option SQLite would otherwise accept as a PRAGMA that does nothing.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class OptionsValidationTests
{
    [Fact]
    public void Validate_WithDefaultFileOptions_DoesNotThrow()
    {
        DocumentStoreOptions.ForFile("some.db").Validate();
    }

    [Fact]
    public void Validate_WithTheInMemoryPresets_DoesNotThrow()
    {
        DocumentStoreOptions.ForInMemory().Validate();
        DocumentStoreOptions.ForSharedInMemory("lds-validate-preset").Validate();
    }

    [Theory]
    [InlineData(1000)]   // not a power of 2
    [InlineData(256)]    // below the minimum
    [InlineData(131072)] // above the maximum
    [InlineData(-4096)]
    public void Validate_WithAPageSizeSqliteWouldIgnore_ThrowsNamingPageSize(int pageSize)
    {
        var options = DocumentStoreOptions.ForFile("some.db");
        options.PageSize = pageSize;

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(nameof(DocumentStoreOptions.PageSize), ex.ParamName);
    }

    [Fact]
    public void Validate_WithAPageSizeOfZero_DoesNotThrow()
    {
        var options = DocumentStoreOptions.ForFile("some.db");
        options.PageSize = 0;

        options.Validate();
    }

    [Fact]
    public void Validate_WithANegativeBusyTimeout_ThrowsNamingBusyTimeout()
    {
        var options = DocumentStoreOptions.ForFile("some.db");
        options.BusyTimeoutMs = -1;

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(nameof(DocumentStoreOptions.BusyTimeoutMs), ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_WithABlankAdditionalPragma_ThrowsNamingTheIndex(string? pragma)
    {
        var options = DocumentStoreOptions.ForFile("some.db");
        options.AdditionalPragmas.Add("PRAGMA temp_store = MEMORY;");
        options.AdditionalPragmas.Add(pragma!);

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(nameof(DocumentStoreOptions.AdditionalPragmas), ex.ParamName);
        Assert.Contains("index 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WithNoConnectionString_ThrowsNamingTheConnectionString()
    {
        var ex = Assert.Throws<ArgumentException>(new DocumentStoreOptions().Validate);
        Assert.Equal(nameof(DocumentStoreOptions.ConnectionString), ex.ParamName);
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=lds-private;Mode=Memory")]
    [InlineData("Data Source=file:lds-private?mode=memory")]
    // Measured: Microsoft.Data.Sqlite gives a second connection an empty database here, because
    // SQLite shares an in-memory database only through a URI filename. Cache=Shared does not make
    // an unadorned ":memory:" shared.
    [InlineData("Data Source=:memory:;Cache=Shared")]
    // The URI spelling of ":memory:", which substring matching read as a file database.
    [InlineData("Data Source=file::memory:")]
    // An empty URI filename: private per connection however shared the cache claims to be.
    [InlineData("Data Source=file:?mode=memory&cache=shared")]
    // No Data Source at all leaves the same empty filename behind the keyword form.
    [InlineData("Mode=Memory;Cache=Shared")]
    // A repeated query parameter takes its last value, so this one opens private.
    [InlineData("Data Source=file:lds-last-wins?mode=memory&cache=shared&cache=private")]
    // The query beats the keyword where it states the parameter.
    [InlineData("Data Source=file:lds-query-wins?mode=memory&cache=private;Cache=Shared")]
    // A raw path that is not empty but decodes to one: SQLite truncates the filename at the NUL.
    [InlineData("Data Source=file:%00?mode=memory&cache=shared")]
    [InlineData("Data Source=file:%00x?mode=memory&cache=shared")]
    // SQLite discards the fragment and everything after it, so this cache=shared is never read.
    [InlineData("Data Source=file:lds-fragment?mode=memory#ignored&cache=shared")]
    public void Validate_WithAPrivateInMemoryDatabase_Throws(string connectionString)
    {
        var options = new DocumentStoreOptions(connectionString) { EnableWalMode = false };

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Contains("private in-memory database", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // A named URI filename with a shared cache, in each spelling SQLite honours.
    [InlineData("Data Source=file:lds-shared?mode=memory&cache=shared")]
    [InlineData("Data Source=file::memory:?cache=shared")]
    [InlineData("Data Source=lds-shared;Mode=Memory;Cache=Shared")]
    // The keyword fills in what the query omits.
    [InlineData("Data Source=file:lds-shared?mode=memory;Cache=Shared")]
    // A percent-escape that decodes to a real filename stays a real filename.
    [InlineData("Data Source=file:a%20?mode=memory&cache=shared")]
    // "%23" is data, not a fragment boundary: the split happens on the raw string, before any
    // decoding. Measured, this opens shared in-memory under the filename "lds-hash#b".
    [InlineData("Data Source=file:lds-hash%23b?mode=memory&cache=shared")]
    // SQLite decodes query keys and values alike.
    [InlineData("Data Source=file:lds-shared?mode=memory&cach%65=shared")]
    [InlineData("Data Source=file:lds-shared?mode=memory&cache=shar%65d")]
    // Not in-memory at all: SQLite is case-sensitive here and fails to open these with Error 14,
    // so the guard leaves them to SQLite rather than blaming the store's own options.
    [InlineData("Data Source=FILE::MEMORY:")]
    [InlineData("Data Source=:MEMORY:")]
    [InlineData("Data Source=file:lds-upper?mode=MEMORY")]
    // Measured: an uppercase prefix is not a URI at all — Windows opened a file called "FILE" —
    // so the query behind it names nothing, empty filename included.
    [InlineData("Data Source=FILE:?mode=memory&cache=shared")]
    public void Validate_WithADatabaseTheStoreCanPool_DoesNotThrow(string connectionString)
    {
        new DocumentStoreOptions(connectionString) { EnableWalMode = false }.Validate();
    }

    [Theory]
    // "+" and a malformed escape are literal to SQLite, so neither names the shared cache mode —
    // measured, both fail with "no such cache mode". They are private in-memory to the guard.
    [InlineData("Data Source=file:lds-plus?mode=memory&cache=shared+")]
    [InlineData("Data Source=file:lds-plus?mode=memory&cache=+shared")]
    [InlineData("Data Source=file:lds-malformed?mode=memory&cache=shar%zzd")]
    // The other half of "%23 is data": decoded into a value it stays a character, and SQLite then
    // fails with "no such cache mode: shar#ed" — so the guard refuses it as private first.
    [InlineData("Data Source=file:lds-hash?mode=memory&cache=shar%23ed")]
    public void Validate_WithACacheModeSqliteWouldNotRead_ThrowsAsPrivate(string connectionString)
    {
        var options = new DocumentStoreOptions(connectionString) { EnableWalMode = false };

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Contains("private in-memory database", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Data Source=lds-wal-memory;Mode=Memory;Cache=Shared")]
    [InlineData("Data Source=file:lds-wal-memory?mode=memory&cache=shared")]
    [InlineData("Data Source=file::memory:?cache=shared")]
    // The Mode= keyword fills in what the query omits: measured, this opens shared in-memory.
    [InlineData("Data Source=file:lds-wal-memory?cache=shared;Mode=Memory")]
    // Still in-memory with a "%23" in the filename — decoding before the fragment split would
    // swallow the query and lose that.
    [InlineData("Data Source=file:lds-wal%23memory?mode=memory&cache=shared")]
    public void Validate_WithWalModeOnAnInMemoryDatabase_Throws(string connectionString)
    {
        // SQLite answers PRAGMA journal_mode = WAL with "memory" here: not an error, not honoured.
        var options = new DocumentStoreOptions(connectionString) { EnableWalMode = true };

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Contains("cannot use WAL mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WithWalModeOffOnAnInMemoryDatabase_DoesNotThrow()
    {
        new DocumentStoreOptions("Data Source=file:lds-no-wal?mode=memory&cache=shared")
        {
            EnableWalMode = false,
        }.Validate();
    }

    [Fact]
    public void Build_WithWalModeOnAnInMemoryDatabase_Throws()
    {
        var builder = DocumentStoreOptions
            .Builder("Data Source=file:lds-builder-wal?mode=memory&cache=shared")
            .WithWalMode();

        Assert.Throws<ArgumentException>(() => builder.Build());
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(131072)]
    public void WithPageSize_WithAValueSqliteWouldIgnore_Throws(int pageSize)
    {
        Assert.Throws<ArgumentException>(() => DocumentStoreOptions.Builder("some.db").WithPageSize(pageSize));
    }

    [Fact]
    public void WithPageSize_WithZero_IsAccepted()
    {
        var options = DocumentStoreOptions.Builder("Data Source=some.db").WithPageSize(0).Build();

        Assert.Equal(0, options.PageSize);
    }

    [Fact]
    public void WithBusyTimeout_WithANegativeValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DocumentStoreOptions.Builder("some.db").WithBusyTimeout(-1));
    }

    [Fact]
    public void PoolWaitTimeout_DefaultsToThirtySeconds()
    {
        Assert.Equal(30_000, new DocumentStoreOptions("Data Source=some.db").PoolWaitTimeoutMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void PoolWaitTimeout_WithAnUnusableValue_ThrowsNamingTheOption(int timeoutMs)
    {
        // Rejected at the setter, like MaxPoolSize: SemaphoreSlim's own exception names
        // "millisecondsTimeout" and never mentions which option was wrong. Validate() repeats the
        // check for the same belt-and-braces reason it repeats MaxPoolSize's.
        var options = DocumentStoreOptions.ForFile("some.db");

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.PoolWaitTimeoutMs = timeoutMs);
        Assert.Equal(30_000, options.PoolWaitTimeoutMs);
        Assert.Contains("-1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WithAnInfinitePoolWaitTimeout_DoesNotThrow()
    {
        var options = DocumentStoreOptions.ForFile("some.db");
        options.PoolWaitTimeoutMs = Timeout.Infinite;

        options.Validate();
        Assert.Equal(Timeout.Infinite, options.PoolWaitTimeoutMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void WithPoolWaitTimeout_WithAnUnusableValue_Throws(int timeoutMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DocumentStoreOptions.Builder("some.db").WithPoolWaitTimeout(timeoutMs));
    }

    [Fact]
    public void PageSizeGuard_WithTheRequestedSize_DoesNotThrow()
    {
        SqlitePageSizeGuard.Validate(4096, "4096");
    }

    [Fact]
    public void PageSizeGuard_WithADifferentSize_ThrowsCarryingBothSizes()
    {
        var ex = Assert.Throws<IncompatiblePageSizeException>(() => SqlitePageSizeGuard.Validate(8192, "4096"));

        Assert.Equal(8192, ex.RequestedPageSize);
        Assert.Equal(4096, ex.ActualPageSize);
        Assert.Contains("VACUUM", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a number")]
    public void PageSizeGuard_WithAnUnreadableReading_Throws(string? reported)
    {
        var ex = Assert.Throws<IncompatiblePageSizeException>(() => SqlitePageSizeGuard.Validate(4096, reported));

        Assert.Equal(4096, ex.RequestedPageSize);
    }
}
