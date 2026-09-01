using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The guard every store wraps its naming convention in. Two types sharing one table is silent
/// cross-type overwriting, so the second type to claim a name is refused rather than served.
/// </summary>
/// <remarks>
/// It exists because <see cref="DefaultTableNamingConvention"/>'s fold is deliberately
/// collision-resistant rather than injective, and because a caller-supplied convention can collide in
/// ways no encoding in this library could prevent — which is why the guard wraps the configured
/// convention rather than living inside the default one.
/// </remarks>
[Trait("Category", "Unit")]
public class TableNameCollisionGuardTests
{
    private sealed record First(string Name);

    private sealed record Second(int Value);

    /// <summary>Maps every type onto one table, the shape the guard exists to catch.</summary>
    private sealed class CollidingConvention : ITableNamingConvention
    {
        public string GetTableName<T>() => "OneTable";

        public string GetTableName(Type type) => "OneTable";
    }

    private static TableNameCollisionGuard Guard(ITableNamingConvention inner) => new(inner);

    [Fact]
    public void GetTableName_ForTheSameTypeRepeatedly_KeepsWorking()
    {
        var guard = Guard(new CollidingConvention());
        Type first = typeof(First);

        Assert.Equal("OneTable", guard.GetTableName<First>());
        Assert.Equal("OneTable", guard.GetTableName<First>());
        Assert.Equal("OneTable", guard.GetTableName(first));
    }

    [Fact]
    public void GetTableName_ForASecondTypeClaimingTheSameName_Throws()
    {
        var guard = Guard(new CollidingConvention());
        guard.GetTableName<First>();

        var exception = Assert.Throws<InvalidOperationException>(() => guard.GetTableName<Second>());

        Assert.Contains("OneTable", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(First), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(Second), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <see cref="Type"/> overload shares the registry, so a collision is caught whichever
    /// overload each of the two types arrived through.
    /// </summary>
    [Fact]
    public void GetTableName_AcrossBothOverloads_SharesTheRegistry()
    {
        var guard = Guard(new CollidingConvention());
        Type first = typeof(First);
        guard.GetTableName(first);

        Assert.Throws<InvalidOperationException>(() => guard.GetTableName<Second>());
    }

    /// <summary>
    /// The collision the default fold deliberately admits, caught here rather than on disk.
    /// </summary>
    [Fact]
    public void GetTableName_ForTheFoldsAcceptedCollision_Throws()
    {
        var guard = Guard(DefaultTableNamingConvention.Instance);
        guard.GetTableName<Naming_.B>();

        Assert.Throws<InvalidOperationException>(() => guard.GetTableName<Naming._B>());
    }

    [Fact]
    public void GetTableName_ForDistinctNames_DoesNotInterfere()
    {
        var guard = Guard(DefaultTableNamingConvention.Instance);

        Assert.NotEqual(guard.GetTableName<NamingProbeA.Customer>(), guard.GetTableName<NamingProbeB.Customer>());
    }

    [Fact]
    public void GetTableName_WithANullType_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Guard(DefaultTableNamingConvention.Instance).GetTableName(null!));
}
