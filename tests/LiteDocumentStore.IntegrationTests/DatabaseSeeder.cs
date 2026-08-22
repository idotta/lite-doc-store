using Bogus;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Provides utilities for seeding test databases with sample data.
/// Uses Bogus library for generating realistic test data.
/// </summary>
public static class DatabaseSeeder
{
    /// <summary>
    /// Seeds the database with a specified number of person records.
    /// </summary>
    public static async Task SeedPersonsAsync(IDocumentStore store, int count = 10, string? idPrefix = null)
    {
        await store.CreateTableAsync<PersonEntity>();

        var faker = new Faker<PersonEntity>()
            .RuleFor(p => p.FirstName, f => f.Name.FirstName())
            .RuleFor(p => p.LastName, f => f.Name.LastName())
            .RuleFor(p => p.Email, f => f.Internet.Email())
            .RuleFor(p => p.Age, f => f.Random.Int(18, 80))
            .RuleFor(p => p.City, f => f.Address.City())
            .RuleFor(p => p.Country, f => f.Address.Country())
            .RuleFor(p => p.PhoneNumber, f => f.Phone.PhoneNumber())
            .RuleFor(p => p.IsActive, f => f.Random.Bool(0.8f)); // 80% active

        var prefix = idPrefix ?? "person";
        var persons = faker.Generate(count);

        var items = persons.Select((person, index) => ($"{prefix}-{index + 1}", person));
        await store.UpsertManyAsync(items);
    }

    /// <summary>
    /// Seeds the database with a specified number of product records.
    /// </summary>
    public static async Task SeedProductsAsync(IDocumentStore store, int count = 10, string? idPrefix = null)
    {
        await store.CreateTableAsync<ProductEntity>();

        var faker = new Faker<ProductEntity>()
            .RuleFor(p => p.Name, f => f.Commerce.ProductName())
            .RuleFor(p => p.Description, f => f.Commerce.ProductDescription())
            .RuleFor(p => p.Price, f => decimal.Parse(f.Commerce.Price()))
            .RuleFor(p => p.Category, f => f.Commerce.Categories(1)[0])
            .RuleFor(p => p.Sku, f => f.Commerce.Ean13())
            .RuleFor(p => p.InStock, f => f.Random.Bool(0.7f)) // 70% in stock
            .RuleFor(p => p.Quantity, f => f.Random.Int(0, 100))
            .RuleFor(p => p.Tags, f => f.Commerce.Categories(3).ToList());

        var prefix = idPrefix ?? "product";
        var products = faker.Generate(count);

        var items = products.Select((product, index) => ($"{prefix}-{index + 1}", product));
        await store.UpsertManyAsync(items);
    }

    /// <summary>
    /// Seeds the database with a specified number of order records.
    /// </summary>
    public static async Task SeedOrdersAsync(IDocumentStore store, int count = 10, string? idPrefix = null)
    {
        await store.CreateTableAsync<OrderEntity>();

        var faker = new Faker<OrderEntity>()
            .RuleFor(o => o.OrderNumber, f => f.Random.AlphaNumeric(10).ToUpper())
            .RuleFor(o => o.CustomerId, f => $"customer-{f.Random.Int(1, 100)}")
            .RuleFor(o => o.OrderDate, f => f.Date.Past(1))
            .RuleFor(o => o.ShippingDate, (f, o) => f.Date.Between(o.OrderDate, o.OrderDate.AddDays(7)))
            .RuleFor(o => o.Status, f => f.PickRandom("Pending", "Processing", "Shipped", "Delivered", "Cancelled"))
            .RuleFor(o => o.TotalAmount, f => f.Random.Decimal(10, 1000))
            .RuleFor(o => o.ShippingAddress, f => new AddressEntity
            {
                Street = f.Address.StreetAddress(),
                City = f.Address.City(),
                State = f.Address.State(),
                ZipCode = f.Address.ZipCode(),
                Country = f.Address.Country()
            })
            .RuleFor(o => o.Items, f => Enumerable.Range(1, f.Random.Int(1, 5))
                .Select(i => new OrderItemEntity
                {
                    ProductId = $"product-{f.Random.Int(1, 100)}",
                    Quantity = f.Random.Int(1, 5),
                    Price = f.Random.Decimal(10, 200)
                })
                .ToList());

        var prefix = idPrefix ?? "order";
        var orders = faker.Generate(count);

        var items = orders.Select((order, index) => ($"{prefix}-{index + 1}", order));
        await store.UpsertManyAsync(items);
    }

    /// <summary>
    /// Seeds the database with a specified number of blog post records.
    /// </summary>
    public static async Task SeedBlogPostsAsync(IDocumentStore store, int count = 10, string? idPrefix = null)
    {
        await store.CreateTableAsync<BlogPostEntity>();

        var faker = new Faker<BlogPostEntity>()
            .RuleFor(b => b.Title, f => f.Lorem.Sentence(3, 5))
            .RuleFor(b => b.Content, f => f.Lorem.Paragraphs(3, 8))
            .RuleFor(b => b.Excerpt, f => f.Lorem.Sentence(10, 20))
            .RuleFor(b => b.AuthorId, f => $"user-{f.Random.Int(1, 20)}")
            .RuleFor(b => b.PublishedDate, f => f.Date.Past(2))
            .RuleFor(b => b.LastModifiedDate, (f, b) => f.Date.Between(b.PublishedDate, DateTime.Now))
            .RuleFor(b => b.Status, f => f.PickRandom("Draft", "Published", "Archived"))
            .RuleFor(b => b.ViewCount, f => f.Random.Int(0, 10000))
            .RuleFor(b => b.Tags, f => f.Lorem.Words(f.Random.Int(2, 5)).ToList())
            .RuleFor(b => b.IsPublished, (f, b) => b.Status == "Published");

        var prefix = idPrefix ?? "post";
        var posts = faker.Generate(count);

        var items = posts.Select((post, index) => ($"{prefix}-{index + 1}", post));
        await store.UpsertManyAsync(items);
    }

    /// <summary>
    /// Seeds the database with multiple entity types for comprehensive testing.
    /// </summary>
    public static async Task SeedAllAsync(IDocumentStore store, int countPerType = 10)
    {
        await SeedPersonsAsync(store, countPerType);
        await SeedProductsAsync(store, countPerType);
        await SeedOrdersAsync(store, countPerType);
        await SeedBlogPostsAsync(store, countPerType);
    }

    /// <summary>
    /// Creates a hierarchical data structure with related entities.
    /// Useful for testing queries and relationships.
    /// </summary>
    public static async Task SeedHierarchicalDataAsync(IDocumentStore store, int customerCount = 5)
    {
        // Seed customers
        await SeedPersonsAsync(store, customerCount, "customer");

        // For each customer, seed orders
        for (int i = 1; i <= customerCount; i++)
        {
            var ordersPerCustomer = new Random().Next(2, 6);
            await store.CreateTableAsync<OrderEntity>();

            var faker = new Faker<OrderEntity>()
                .RuleFor(o => o.OrderNumber, f => f.Random.AlphaNumeric(10).ToUpper())
                .RuleFor(o => o.CustomerId, $"customer-{i}")
                .RuleFor(o => o.OrderDate, f => f.Date.Past(1))
                .RuleFor(o => o.Status, f => f.PickRandom("Pending", "Processing", "Shipped", "Delivered"))
                .RuleFor(o => o.TotalAmount, f => f.Random.Decimal(50, 500))
                .RuleFor(o => o.Items, f => Enumerable.Range(1, f.Random.Int(1, 3))
                    .Select(j => new OrderItemEntity
                    {
                        ProductId = $"product-{f.Random.Int(1, 10)}",
                        Quantity = f.Random.Int(1, 3),
                        Price = f.Random.Decimal(10, 100)
                    })
                    .ToList());

            var orders = faker.Generate(ordersPerCustomer);
            var items = orders.Select((order, index) =>
                ($"order-customer{i}-{index + 1}", order));
            await store.UpsertManyAsync(items);
        }

        // Seed products referenced in orders
        await SeedProductsAsync(store, 10);
    }
}

// Test entity models

public class PersonEntity
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int Age { get; set; }
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public class ProductEntity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public bool InStock { get; set; }
    public int Quantity { get; set; }
    public List<string> Tags { get; set; } = new();
}

public class OrderEntity
{
    public string OrderNumber { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public DateTime? ShippingDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public AddressEntity ShippingAddress { get; set; } = new();
    public List<OrderItemEntity> Items { get; set; } = new();
}

public class AddressEntity
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}

public class OrderItemEntity
{
    public string ProductId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}

public class BlogPostEntity
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Excerpt { get; set; } = string.Empty;
    public string AuthorId { get; set; } = string.Empty;
    public DateTime PublishedDate { get; set; }
    public DateTime LastModifiedDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public int ViewCount { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool IsPublished { get; set; }
}
