// Probe types for DefaultTableNamingConventionTests. They live at namespace scope, in block
// namespaces, because the cases being pinned are boundaries between a namespace and a type name —
// which a type nested inside the test class cannot express — and one case needs the global namespace.

/// <summary>A type in the global namespace, which keeps its bare name.</summary>
internal sealed record GlobalNamingProbeDoc(string Name);

namespace LiteDocumentStore.UnitTests.NamingProbeA
{
    internal sealed record Customer(string Email);
}

namespace LiteDocumentStore.UnitTests.NamingProbeB
{
    internal sealed record Customer(int Score);
}

namespace LiteDocumentStore.UnitTests.Naming_
{
    /// <summary>Folds to <c>…_Naming___B</c>, the same name as <see cref="Naming._B"/>.</summary>
    internal sealed record B(string Name);
}

namespace LiteDocumentStore.UnitTests.Naming
{
    /// <summary>Folds to <c>…_Naming___B</c>, the same name as <see cref="Naming_.B"/>.</summary>
    internal sealed record _B(string Name);
}

namespace LiteDocumentStore.UnitTests.NamingProbeNesting
{
    internal sealed class Outer<T>
    {
        internal sealed record Inner(string Name);
    }
}
