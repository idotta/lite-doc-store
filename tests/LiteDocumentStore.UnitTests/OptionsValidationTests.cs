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
    public void Validate_WithAPrivateInMemoryDatabase_Throws(string connectionString)
    {
        var options = new DocumentStoreOptions(connectionString) { EnableWalMode = false };

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Contains("private in-memory database", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Data Source=lds-wal-memory;Mode=Memory;Cache=Shared")]
    [InlineData("Data Source=file:lds-wal-memory?mode=memory&cache=shared")]
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
