using System.Runtime.Serialization;
using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Pins the rename that resolved the simple-name collision with
/// <see cref="SerializationException"/>: both bare names have to be usable in one file. The
/// assertions are secondary — reverting the rename makes this file fail to compile, CS0246 on
/// the new name and CS0104 on the old one.
/// </summary>
[Trait("Category", "Unit")]
public class ExceptionNameCollisionTests
{
    [Fact]
    public void BothBareExceptionNames_ResolveWithoutAmbiguity()
    {
        Assert.Equal("LiteDocumentStore.Exceptions", typeof(DocumentSerializationException).Namespace);
        Assert.Equal("System.Runtime.Serialization", typeof(SerializationException).Namespace);
    }
}
