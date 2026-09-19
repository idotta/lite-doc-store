using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests pinning that <see cref="MigrationHistoryRecord"/>'s properties are init-only. The type
/// is a snapshot of one history row, rebuilt on every read, so a settable property advertises a
/// mutability nothing persists.
/// </summary>
/// <remarks>
/// These assert through reflection — established practice in this project, see the comment in
/// <c>OptionsSnapshotTests.Clone_CopiesEverySettablePublicProperty</c>. The trap is that an
/// init-only property's setter is <em>not</em> null: what marks it init-only is a required custom
/// modifier <see cref="IsExternalInit"/> on the setter's return parameter.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class MigrationHistoryRecordTests
{
    [Theory]
    [InlineData(nameof(MigrationHistoryRecord.Version))]
    [InlineData(nameof(MigrationHistoryRecord.Name))]
    [InlineData(nameof(MigrationHistoryRecord.AppliedAt))]
    [InlineData(nameof(MigrationHistoryRecord.Checksum))]
    public void Property_OnMigrationHistoryRecord_IsInitOnly(string propertyName)
    {
        var property = typeof(MigrationHistoryRecord).GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(property);
        Assert.NotNull(property!.SetMethod);
        Assert.True(
            IsInitOnly(property),
            $"{propertyName} has a freely settable setter; it must be init-only.");
    }

    [Fact]
    public void Properties_OnMigrationHistoryRecord_AreAllInitOnly()
    {
        var properties = typeof(MigrationHistoryRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(properties);

        var settable = properties
            .Where(p => p.SetMethod is { IsPublic: true } && !IsInitOnly(p))
            .Select(p => p.Name)
            .ToArray();

        Assert.True(
            settable.Length == 0,
            $"These properties have a freely settable public setter: {string.Join(", ", settable)}");
    }

    private static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod is { } setter
        && setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
}
