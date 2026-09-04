using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;
using Northstar.Secrets.Infrastructure;
using Northstar.Secrets.Infrastructure.Persistence;
using Northstar.Secrets.Infrastructure.Security;

namespace Northstar.Secrets.UnitTests;

internal sealed class FakeClock(DateTimeOffset initial) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = initial;
    public void Advance(TimeSpan amount) => UtcNow = UtcNow.Add(amount);
    public void Set(DateTimeOffset value) => UtcNow = value;
}

internal sealed class AcceptingNotificationChannel : INotificationChannel
{
    public string Name => "test";
    public List<RotationNotice> Notices { get; } = [];

    public Task<NotificationDeliveryResult> SendAsync(
        Consumer consumer,
        RotationNotice notice,
        CancellationToken cancellationToken)
    {
        Notices.Add(notice);
        return Task.FromResult(new NotificationDeliveryResult(true, 1, null));
    }
}

internal sealed class TestHarness : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private TestHarness() { }

    public FakeClock Clock { get; } =
        new(new DateTimeOffset(2026, 9, 3, 8, 0, 0, TimeSpan.Zero));
    public SecretsDbContext Db { get; private set; } = null!;
    public EfSecretsRepository Repository { get; private set; } = null!;
    public LocalMasterKeyProvider KeyProvider { get; private set; } = null!;
    public EnvelopeEncryptionService Cipher { get; private set; } = null!;
    public SecretGeneratorRegistry Generators { get; private set; } = null!;
    public SimulatedSecretVerifier Verifier { get; } = new();
    public SecretRedactionRegistry Redactor { get; } = new();
    public AcceptingNotificationChannel Notifications { get; } = new();
    public SecretLifecycleService Lifecycle { get; private set; } = null!;
    public RotationEngine Engine { get; private set; } = null!;

    public static async Task<TestHarness> CreateAsync()
    {
        var harness = new TestHarness();
        await harness._connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SecretsDbContext>()
            .UseSqlite(harness._connection)
            .Options;
        harness.Db = new SecretsDbContext(options);
        await harness.Db.Database.EnsureCreatedAsync();
        harness.Repository = new EfSecretsRepository(harness.Db);
        harness.KeyProvider = new LocalMasterKeyProvider(new LocalMasterKeyProviderOptions
        {
            KeyVersion = "test-v1",
            MasterKey = "demo-only-not-a-real-secret-unit-test-master-key"
        });
        harness.Cipher = new EnvelopeEncryptionService(harness.KeyProvider);
        harness.Generators = new SecretGeneratorRegistry(
        [
            new PasswordSecretGenerator(),
            new ApiKeySecretGenerator(),
            new EncryptionKeySecretGenerator(),
            new ConnectionStringSecretGenerator(),
            new KeyPairSecretGenerator(),
            new CertificateSecretGenerator()
        ]);
        harness.Lifecycle = new SecretLifecycleService(
            harness.Repository,
            harness.Generators,
            harness.Cipher,
            harness.KeyProvider,
            harness.Clock,
            new PathPolicyEvaluator(),
            harness.Redactor,
            new NullPlatformMetrics());
        harness.Engine = harness.CreateEngine();
        return harness;
    }

    public RotationEngine CreateEngine(TimeSpan? acknowledgementTimeout = null) =>
        new(
            Repository,
            Generators,
            Cipher,
            Verifier,
            [new DualWriteRotationStrategy(), new SingleCutoverRotationStrategy()],
            [Notifications],
            Redactor,
            new NullPlatformMetrics(),
            Clock,
            new RotationEngineOptions
            {
                AcknowledgementTimeout = acknowledgementTimeout ?? TimeSpan.FromMinutes(10)
            });

    public async Task<RegisteredSecret> RegisterAsync(
        string name = "orders/prod/database",
        SecretType type = SecretType.DatabasePassword,
        IReadOnlyList<Guid>? consumerIds = null,
        IReadOnlyList<string>? tags = null,
        int rotationHours = 24,
        int maxAgeHours = 72) =>
        await Lifecycle.RegisterAsync(
            new RegisterSecretCommand(
                name,
                type,
                "Northstar Platform Engineering (fictional)",
                "prod",
                SecretCriticality.High,
                tags ?? ["synthetic"],
                "Synthetic test secret.",
                rotationHours,
                maxAgeHours,
                1,
                consumerIds),
            CancellationToken.None);

    public async Task<Consumer> AddConsumerAsync(string application = "orders")
    {
        var consumer = new Consumer(
            Guid.NewGuid(),
            $"consumer-{Guid.NewGuid():N}",
            application,
            "https://webhook.example.invalid/rotation",
            "consumer@example.invalid");
        await Repository.AddConsumerAsync(consumer, CancellationToken.None);
        await Repository.SaveChangesAsync(CancellationToken.None);
        return consumer;
    }

    public async Task AddReadPolicyAsync(string actor, string pattern)
    {
        await Repository.AddAccessPolicyAsync(
            new AccessPolicy(Guid.NewGuid(), actor, pattern, false, true, false, false),
            CancellationToken.None);
        await Repository.SaveChangesAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
