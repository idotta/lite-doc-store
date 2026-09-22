// Two document types with the same simple name, in different namespaces — the collision the table-name
// guard exists to refuse, the shape that silently overwrites one type's rows with another's. They live
// at namespace scope because that is the only way to express the collision.

namespace LiteDocumentStore.IntegrationTests.NamingA
{
    internal sealed record Customer(string Id, string Email);
}

namespace LiteDocumentStore.IntegrationTests.NamingB
{
    internal sealed record Customer(string Id, int Score);
}
