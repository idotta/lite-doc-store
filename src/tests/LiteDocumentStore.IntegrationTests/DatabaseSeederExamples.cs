using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Example tests demonstrating the use of DatabaseSeeder utilities.
/// </summary>
[Collection(nameof(LiteDocumentStoreCollection))]
public class DatabaseSeederExamples
{
    private readonly LiteDocumentStoreTestFixture _fixture;

    public DatabaseSeederExamples(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SeedPersons_CreatesMultipleRecords()
    {
        // Arrange
        var store = await _fixture.CreateInMemoryStoreAsync();

        // Act
        await DatabaseSeeder.SeedPersonsAsync(store, count: 50);

        // Assert
        var count = await store.CountAsync<PersonEntity>();
        Assert.Equal(50, count);

        // Verify data quality
        var allPersons = await store.GetAllAsync<PersonEntity>();
        var persons = allPersons.ToList();

        Assert.All(persons, person =>
        {
            Assert.NotEmpty(person.FirstName);
            Assert.NotEmpty(person.LastName);
            Assert.NotEmpty(person.Email);
            Assert.InRange(person.Age, 18, 80);
        });
    }

    [Fact]
    public async Task SeedProducts_CreatesProductsWithCategories()
    {
        // Arrange
        var store = await _fixture.CreateInMemoryStoreAsync();

        // Act
        await DatabaseSeeder.SeedProductsAsync(store, count: 30);

        // Assert
        var count = await store.CountAsync<ProductEntity>();
        Assert.Equal(30, count);

        // Verify products have required data
        var allProducts = await store.GetAllAsync<ProductEntity>();
        var products = allProducts.ToList();

        Assert.All(products, product =>
        {
            Assert.NotEmpty(product.Name);
            Assert.True(product.Price > 0);
            Assert.NotEmpty(product.Category);
        });
    }

    [Fact]
    public async Task SeedOrders_CreatesOrdersWithItems()
    {
        // Arrange
        var store = await _fixture.CreateInMemoryStoreAsync();

        // Act
        await DatabaseSeeder.SeedOrdersAsync(store, count: 20);

        // Assert
        var count = await store.CountAsync<OrderEntity>();
        Assert.Equal(20, count);

        // Verify orders have items
        var allOrders = await store.GetAllAsync<OrderEntity>();
        var orders = allOrders.ToList();

        Assert.All(orders, order =>
        {
            Assert.NotEmpty(order.OrderNumber);
            Assert.NotEmpty(order.CustomerId);
            Assert.NotEmpty(order.Items);
            Assert.True(order.TotalAmount > 0);
        });
    }

    [Fact]
    public async Task SeedAll_CreatesMultipleEntityTypes()
    {
        // Arrange
        var store = await _fixture.CreateInMemoryStoreAsync();

        // Act
        await DatabaseSeeder.SeedAllAsync(store, countPerType: 10);

        // Assert - Verify all entity types were created
        var personCount = await store.CountAsync<PersonEntity>();
        var productCount = await store.CountAsync<ProductEntity>();
        var orderCount = await store.CountAsync<OrderEntity>();
        var blogPostCount = await store.CountAsync<BlogPostEntity>();

        Assert.Equal(10, personCount);
        Assert.Equal(10, productCount);
        Assert.Equal(10, orderCount);
        Assert.Equal(10, blogPostCount);
    }

    [Fact]
    public async Task SeedHierarchicalData_CreatesRelatedEntities()
    {
        // Arrange
        var store = await _fixture.CreateInMemoryStoreAsync();

        // Act
        await DatabaseSeeder.SeedHierarchicalDataAsync(store, customerCount: 5);

        // Assert
        var personCount = await store.CountAsync<PersonEntity>();
        var orderCount = await store.CountAsync<OrderEntity>();
        var productCount = await store.CountAsync<ProductEntity>();

        Assert.Equal(5, personCount); // 5 customers
        Assert.True(orderCount >= 10); // At least 2 orders per customer (random 2-6)
        Assert.Equal(10, productCount);

        // Verify customers have orders
        for (int i = 1; i <= 5; i++)
        {
            var customerId = $"customer-{i}";
            var person = await store.GetAsync<PersonEntity>(customerId);
            Assert.NotNull(person);
        }
    }

    [Fact]
    public async Task Seeder_WithCustomPrefix_UsesCustomIds()
    {
        // Arrange
        var store = await _fixture.CreateInMemoryStoreAsync();

        // Act
        await DatabaseSeeder.SeedPersonsAsync(store, count: 5, idPrefix: "user");

        // Assert
        var user1 = await store.GetAsync<PersonEntity>("user-1");
        var user5 = await store.GetAsync<PersonEntity>("user-5");

        Assert.NotNull(user1);
        Assert.NotNull(user5);

        // Default prefix should not exist
        var person1 = await store.GetAsync<PersonEntity>("person-1");
        Assert.Null(person1);
    }

    [Fact]
    public async Task SeededData_CanBeQueried()
    {
        // Arrange
        var store = await _fixture.CreateInMemoryStoreAsync();
        await DatabaseSeeder.SeedPersonsAsync(store, count: 100);

        // Act - Query seeded data
        var allPersons = await store.GetAllAsync<PersonEntity>();
        var persons = allPersons.ToList();

        // Filter active persons
        var activePersons = persons.Where(p => p.IsActive).ToList();

        // Assert
        Assert.NotEmpty(activePersons);
        Assert.True(activePersons.Count > 50); // Should be around 80% based on seeder
        Assert.All(activePersons, p => Assert.True(p.IsActive));
    }

    [Fact]
    public async Task SeededData_SupportsVirtualColumns()
    {
        // Arrange
        var store = await _fixture.CreateFileStoreAsync();
        await DatabaseSeeder.SeedPersonsAsync(store, count: 50);

        // Act - Add virtual column for easier querying
        await store.AddVirtualColumnAsync<PersonEntity>(
            p => p.Age,
            "age",
            createIndex: true);

        // Assert - Virtual column should work with seeded data. Range predicates are no longer
        // expressible through the store API, so seek the indexed virtual column via raw SQL and
        // load each matching document through the public GetAsync.
        var youngIds = await store.Connection.QueryStringsAsync(
            "SELECT id FROM [PersonEntity] WHERE age < @Age",
            ("Age", 30));

        var youngList = new List<PersonEntity>();
        foreach (var id in youngIds)
        {
            var person = await store.GetAsync<PersonEntity>(id!);
            Assert.NotNull(person);
            youngList.Add(person);
        }

        Assert.NotEmpty(youngList);
        Assert.All(youngList, p => Assert.True(p.Age < 30));
    }

    [Fact]
    public async Task MultipleStores_CanSeedIndependently()
    {
        // Arrange
        var store1 = await _fixture.CreateInMemoryStoreAsync();
        var store2 = await _fixture.CreateInMemoryStoreAsync();

        // Act - Seed different data in each store
        await DatabaseSeeder.SeedPersonsAsync(store1, count: 10, idPrefix: "store1");
        await DatabaseSeeder.SeedPersonsAsync(store2, count: 20, idPrefix: "store2");

        // Assert
        var count1 = await store1.CountAsync<PersonEntity>();
        var count2 = await store2.CountAsync<PersonEntity>();

        Assert.Equal(10, count1);
        Assert.Equal(20, count2);

        // Verify isolation
        var person1 = await store1.GetAsync<PersonEntity>("store1-1");
        var person2 = await store2.GetAsync<PersonEntity>("store2-1");
        var crossCheck = await store1.GetAsync<PersonEntity>("store2-1");

        Assert.NotNull(person1);
        Assert.NotNull(person2);
        Assert.Null(crossCheck); // Should not exist in store1
    }
}
