using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for the options snapshot the <see cref="DocumentStore"/> constructor takes: what is
/// validated is what is used, whoever mutates the caller's options object and whenever.
/// </summary>
/// <remarks>
/// <see cref="DocumentStoreFactory"/> validates, then calls a caller-supplied
/// <see cref="ILoggerFactory"/>, then constructs the store — arbitrary caller code holding the same
/// mutable options object. Both DI registrations capture that object by reference, and an ordinary
/// concurrent setter reaches the same window with no custom logger at all.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class OptionsSnapshotTests
{
    [Fact]
    public void CreateStore_WithALoggerFactoryThatRetargetsTheConnectionString_OpensTheValidatedDatabase()
    {
        // Both connection strings are valid and pass every guard: this is retargeting, not bypass.
        const string Validated = "Data Source=file:snapshot-validated?mode=memory&cache=shared";
        const string Retargeted = "Data Source=file:snapshot-retargeted?mode=memory&cache=shared";

        var options = new DocumentStoreOptions { ConnectionString = Validated, EnableWalMode = false };
        var connectionFactory = new RecordingConnectionFactory();
        var loggerFactory = new RetargetingLoggerFactory(options, Retargeted);

        using var store = new DocumentStoreFactory(connectionFactory, null, loggerFactory).Create(options);

        Assert.True(loggerFactory.Fired);
        Assert.Equal(Retargeted, options.ConnectionString);
        Assert.NotEmpty(connectionFactory.RequestedConnectionStrings);
        Assert.All(
            connectionFactory.RequestedConnectionStrings,
            requested =>
            {
                // The pool normalizes the string (quoting, Pooling=False), so the database name is
                // what is compared rather than the literal.
                Assert.Contains("snapshot-validated", requested, StringComparison.Ordinal);
                Assert.DoesNotContain("snapshot-retargeted", requested, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Constructor_WithOptionsMutatedAfterConstructionReturns_KeepsTheValuesItWasBuiltWith()
    {
        // This half is the connection pool's own normalization clone, which predates the
        // constructor snapshot: by the time the constructor has returned, the pool has already
        // copied the options. It is pinned here because the store's observable configuration is
        // what a caller cares about, not which of the two clones produced it — the constructor's
        // own snapshot is pinned by the concurrent test below.
        var options = new DocumentStoreOptions
        {
            ConnectionString = "Data Source=file:snapshot-after?mode=memory&cache=shared",
            EnableWalMode = false,
            MaxPoolSize = 3
        };

        using var store = new DocumentStore(options, new RecordingConnectionFactory());

        options.MaxPoolSize = 11;

        Assert.Equal(3, store.MaxPoolSize);
    }

    [Fact]
    public async Task Constructor_WithOptionsMutatedConcurrently_NeverOpensConnectionsForUnvalidatedOptions()
    {
        // The constructor reading from the snapshot rather than from the caller's object only
        // matters against a writer mutating that object *during* the constructor body, and nothing
        // in that body is injectable — so this is stressed, like SqliteConnectionPoolTests'
        // return-racing-dispose pair.
        //
        // PageSize is the probe because Validate() rejects a non-power-of-2 while the pool's own
        // Normalize does not re-check it (it re-checks only the connection string), so a value that
        // slips in after validation reaches the connection factory instead of being caught again.
        // The invariant is therefore exact: a store either validated 4096 and opens with 4096, or
        // snapshotted 777 and never constructs at all. 777 reaching the factory means the value
        // that was validated is not the value that was used.
        const int attempts = 400;
        const int Valid = 4096;
        const int Invalid = 777;

        var recorded = new ConcurrentBag<int>();
        var constructed = 0;
        var rejected = 0;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var options = new DocumentStoreOptions
            {
                ConnectionString = $"Data Source=file:snapshot-race-{attempt}?mode=memory&cache=shared",
                EnableWalMode = false,
                PageSize = Valid
            };
            var connectionFactory = new PageSizeRecordingConnectionFactory(recorded);
            var builderDone = 0;

            using var ready = new Barrier(2);
            var mutator = Task.Run(() =>
            {
                ready.SignalAndWait();

                // Spins until the builder is done rather than for a fixed count: a fixed loop can
                // finish before the builder is even scheduled, which leaves the attempt overlapping
                // nothing and the test silently vacuous on a faster machine.
                var flip = false;
                while (Volatile.Read(ref builderDone) == 0)
                {
                    options.PageSize = flip ? Valid : Invalid;
                    flip = !flip;
                }

                options.PageSize = Valid;
            });

            var builder = Task.Run(() =>
            {
                ready.SignalAndWait();
                try
                {
                    using var store = new DocumentStore(options, connectionFactory);
                    store.Initialize();
                    Interlocked.Increment(ref constructed);
                }
                catch (ArgumentException ex)
                    when (ex.ParamName == nameof(DocumentStoreOptions.PageSize))
                {
                    // The snapshot caught the invalid value, which is the other legal outcome — and
                    // the direct evidence that this attempt did overlap a mutation. Narrowed to the
                    // probe's own ParamName so an unrelated argument failure is not absorbed.
                    Interlocked.Increment(ref rejected);
                }
                catch (IncompatiblePageSizeException)
                {
                    // Only reachable when an unvalidated page size did reach a connection; the
                    // assertion below is what reports it, because the factory has already recorded.
                }
                finally
                {
                    Volatile.Write(ref builderDone, 1);
                }
            });

            await Task.WhenAll(mutator, builder);
        }

        // Guards the assertion below against passing on a run where nothing was ever built...
        Assert.True(constructed > 0, "no store was constructed, so nothing was proved");
        // ...and against one where no construction ever saw a mutation in flight, which would make
        // the run vacuous: a constructor reading the caller's object would pass it just as happily.
        Assert.True(
            rejected > 0,
            "no construction observed a mid-flight mutation, so the race never happened and this " +
            "test proved nothing");
        Assert.DoesNotContain(Invalid, recorded);
    }

    [Fact]
    public void Constructor_WithOptionsThatFailValidation_ThrowsNamingTheOffendingOption()
    {
        // The backstop is not bypassable by skipping the factory. PageSize rather than MaxPoolSize:
        // MaxPoolSize's own setter rejects 0, so Validate's copy of that check is unreachable here.
        var options = new DocumentStoreOptions
        {
            ConnectionString = "Data Source=file:snapshot-invalid?mode=memory&cache=shared",
            EnableWalMode = false,
            PageSize = 777
        };

        var ex = Assert.Throws<ArgumentException>(
            () => new DocumentStore(options, new RecordingConnectionFactory()));

        Assert.Equal(nameof(DocumentStoreOptions.PageSize), ex.ParamName);
    }

    [Fact]
    public void Clone_CopiesEverySettablePublicProperty()
    {
        // Clone() is load-bearing for every store construction, not just the pool's Normalize: a
        // property left out of it silently reverts to its default on every store. Reflection is
        // fine here — the library is AOT-safe, the test project is not required to be.
        var source = new DocumentStoreOptions();
        var properties = typeof(DocumentStoreOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetSetMethod() is not null && p.GetGetMethod() is not null)
            .ToArray();

        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            property.SetValue(source, NonDefaultValueFor(property));
        }

        var clone = source.Clone();

        foreach (var property in properties)
        {
            var expected = property.GetValue(source);
            var actual = property.GetValue(clone);

            if (expected is List<string> expectedList)
            {
                Assert.Equal(expectedList, Assert.IsType<List<string>>(actual));
                continue;
            }

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task Clone_WithAdditionalPragmasNulledConcurrently_CarriesTheNullThrough()
    {
        // Clone() runs on every store construction now, and reading AdditionalPragmas twice — once
        // for the null check, once for the copy — let a writer nulling it in between fail the copy
        // instead of carrying the null through to the ArgumentException Validate() documents for the
        // option. Measured as ArgumentNullException naming "source", because a collection expression
        // lowers to Enumerable.ToList; NullReferenceException is caught beside it so the assertion
        // does not depend on that lowering. Stressed rather than pinned deterministically, like the
        // constructor race above: the window sits between two reads of one property and nothing in
        // it is injectable.
        const int clones = 200_000;

        var pragmas = new List<string> { "PRAGMA temp_store = MEMORY" };
        var options = new DocumentStoreOptions
        {
            ConnectionString = "Data Source=file:clone-pragma-race?mode=memory&cache=shared",
            AdditionalPragmas = pragmas
        };

        var cloningDone = 0;
        var nullsCarried = 0;
        var dereferenced = 0;

        using var ready = new Barrier(2);
        var mutator = Task.Run(() =>
        {
            ready.SignalAndWait();

            var flip = false;
            while (Volatile.Read(ref cloningDone) == 0)
            {
                options.AdditionalPragmas = flip ? null! : pragmas;
                flip = !flip;
            }

            options.AdditionalPragmas = pragmas;
        });

        var cloner = Task.Run(() =>
        {
            ready.SignalAndWait();
            try
            {
                for (var i = 0; i < clones; i++)
                {
                    try
                    {
                        if (options.Clone().AdditionalPragmas is null)
                        {
                            nullsCarried++;
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentNullException or NullReferenceException)
                    {
                        // Counted rather than thrown so the assertion below names the defect.
                        dereferenced++;
                    }
                }
            }
            finally
            {
                Volatile.Write(ref cloningDone, 1);
            }
        });

        await Task.WhenAll(mutator, cloner);

        Assert.Equal(0, dereferenced);
        // Guards against a vacuous run: a clone that never observed the nulled value never
        // exercised the double read, so the assertion above would prove nothing.
        Assert.True(nullsCarried > 0, "no clone observed a null AdditionalPragmas, so nothing was proved");
    }

    /// <summary>
    /// A value that differs from the property's default, so a property missing from
    /// <see cref="DocumentStoreOptions.Clone"/> shows up as the default rather than as a match.
    /// </summary>
    private static object? NonDefaultValueFor(PropertyInfo property) => property.Name switch
    {
        nameof(DocumentStoreOptions.ConnectionString) => "Data Source=file:clone-probe?mode=memory&cache=shared",
        nameof(DocumentStoreOptions.EnableWalMode) => false,
        nameof(DocumentStoreOptions.SynchronousMode) => SynchronousMode.Full,
        nameof(DocumentStoreOptions.PageSize) => 8192,
        nameof(DocumentStoreOptions.CacheSize) => -4000,
        nameof(DocumentStoreOptions.BusyTimeoutMs) => 1234,
        nameof(DocumentStoreOptions.EnableForeignKeys) => false,
        nameof(DocumentStoreOptions.MaxPoolSize) => 7,
        nameof(DocumentStoreOptions.PoolWaitTimeoutMs) => 4321,
        nameof(DocumentStoreOptions.TableNamingConvention) => DefaultTableNamingConvention.Instance,
        nameof(DocumentStoreOptions.AdditionalPragmas) => new List<string> { "PRAGMA temp_store = MEMORY" },
        nameof(DocumentStoreOptions.SerializerOptions) =>
            new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() },
        // A new property was added without teaching this test a non-default value for it. Add one
        // here, and make sure Clone() copies the property.
        _ => throw new InvalidOperationException(
            $"DocumentStoreOptions.{property.Name} has no non-default probe value in this test. " +
            "Add one, and make sure Clone() copies the property.")
    };

    private sealed class RetargetingLoggerFactory(DocumentStoreOptions options, string retargetTo) : ILoggerFactory
    {
        public bool Fired { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            // Arbitrary caller code, running between Validate() and construction, on the very
            // object that was validated.
            Fired = true;
            options.ConnectionString = retargetTo;
            return NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Records the page size of the options it is handed, so that a value which never passed
    /// <see cref="DocumentStoreOptions.Validate"/> is visible even when a later guard rejects it.
    /// </summary>
    private sealed class PageSizeRecordingConnectionFactory(ConcurrentBag<int> recorded) : IConnectionFactory
    {
        private readonly DefaultConnectionFactory _inner = new();

        public SqliteConnection CreateConnection(DocumentStoreOptions options)
        {
            recorded.Add(options.PageSize);
            return _inner.CreateConnection(options);
        }

        public Task<SqliteConnection> CreateConnectionAsync(
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default)
        {
            recorded.Add(options.PageSize);
            return _inner.CreateConnectionAsync(options, cancellationToken);
        }
    }

    private sealed class RecordingConnectionFactory : IConnectionFactory
    {
        private readonly List<string> _requested = [];

        public IReadOnlyList<string> RequestedConnectionStrings
        {
            get
            {
                lock (_requested)
                {
                    return [.. _requested];
                }
            }
        }

        public SqliteConnection CreateConnection(DocumentStoreOptions options)
        {
            Record(options);

            var connection = new SqliteConnection(options.ConnectionString);
            connection.Open();
            return connection;
        }

        public async Task<SqliteConnection> CreateConnectionAsync(
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default)
        {
            Record(options);

            var connection = new SqliteConnection(options.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        private void Record(DocumentStoreOptions options)
        {
            lock (_requested)
            {
                _requested.Add(options.ConnectionString);
            }
        }
    }
}
