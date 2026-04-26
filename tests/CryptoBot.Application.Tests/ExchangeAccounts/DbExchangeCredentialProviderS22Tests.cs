using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.ExchangeAccountAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using CryptoBot.Infrastructure.ExchangeAccounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoBot.Application.Tests.ExchangeAccounts;

/// <summary>
/// S22 T1c — <see cref="DbExchangeCredentialProvider"/> 金鑰熱切換事件的「同步性」合約。
///
/// Why：BingXExchangeClient 以 CredentialsChanged 事件觸發 SDK client 重建（<c>_clientGate</c>）。
/// 若事件是 fire-and-forget async，NotifyCredentialsChangedAsync 會在舊 client 還沒替換完時就回到 UI，
/// 使用者以為已切換，下一筆 API 請求可能還吃到舊金鑰 → 401 / 成交去錯帳。
///
/// 這裡以同步 handler 檢查「Invoke 完畢 = handler 真的跑過了」，不允許任何隱式 Task 被吞掉。
/// </summary>
[Trait("Category", "S22")]
public class DbExchangeCredentialProviderS22Tests
{
    [Fact]
    public async Task NotifyCredentialsChangedAsync_FiresHandlerSynchronously()
    {
        var repo = new FakeExchangeAccountRepository();
        var account = ExchangeAccount.Create(ExchangeName.BingX, "main", "key-A", "secret-A");
        account.Activate();
        await repo.AddAsync(account);

        var sut = new DbExchangeCredentialProvider(
            new FakeScopeFactory(repo),
            NullLogger<DbExchangeCredentialProvider>.Instance);

        var handlerInvoked = false;
        ExchangeCredentials? observed = null;
        sut.CredentialsChanged += (_, e) =>
        {
            handlerInvoked = true;
            observed = e.Credentials;
        };

        await sut.NotifyCredentialsChangedAsync(ExchangeName.BingX);

        Assert.True(handlerInvoked, "CredentialsChanged must be invoked before NotifyCredentialsChangedAsync returns.");
        Assert.NotNull(observed);
        Assert.True(observed!.IsConfigured);
        Assert.Equal("key-A", observed.ApiKey);
        Assert.Equal("secret-A", observed.ApiSecret);
    }

    [Fact]
    public async Task NotifyCredentialsChangedAsync_PropagatesHandlerException()
    {
        // 同步語意下 handler 拋出例外必須能傳回呼叫端 — 否則 UI 會誤認為切換成功。
        var repo = new FakeExchangeAccountRepository();
        var account = ExchangeAccount.Create(ExchangeName.BingX, "main", "k", "s");
        account.Activate();
        await repo.AddAsync(account);

        var sut = new DbExchangeCredentialProvider(
            new FakeScopeFactory(repo),
            NullLogger<DbExchangeCredentialProvider>.Instance);

        sut.CredentialsChanged += (_, _) => throw new InvalidOperationException("handler-boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.NotifyCredentialsChangedAsync(ExchangeName.BingX));
    }

    [Fact]
    public async Task NotifyCredentialsChangedAsync_EmitsUnconfigured_WhenNoActiveAccount()
    {
        var repo = new FakeExchangeAccountRepository();

        var sut = new DbExchangeCredentialProvider(
            new FakeScopeFactory(repo),
            NullLogger<DbExchangeCredentialProvider>.Instance);

        ExchangeCredentials? observed = null;
        sut.CredentialsChanged += (_, e) => observed = e.Credentials;

        await sut.NotifyCredentialsChangedAsync(ExchangeName.BingX);

        Assert.NotNull(observed);
        Assert.False(observed!.IsConfigured);
        Assert.Equal(string.Empty, observed.ApiKey);
        Assert.Equal(string.Empty, observed.ApiSecret);
    }

    [Fact]
    public async Task NotifyCredentialsChangedAsync_NoSubscribers_DoesNotThrow()
    {
        var repo = new FakeExchangeAccountRepository();
        var sut = new DbExchangeCredentialProvider(
            new FakeScopeFactory(repo),
            NullLogger<DbExchangeCredentialProvider>.Instance);

        var ex = await Record.ExceptionAsync(
            () => sut.NotifyCredentialsChangedAsync(ExchangeName.BingX));

        Assert.Null(ex);
    }

    // ---- Test doubles ----

    private sealed class FakeScopeFactory : IServiceScopeFactory
    {
        private readonly IExchangeAccountRepository _repo;
        public FakeScopeFactory(IExchangeAccountRepository repo) => _repo = repo;

        public IServiceScope CreateScope() => new FakeScope(_repo);

        private sealed class FakeScope : IServiceScope, IServiceProvider
        {
            private readonly IExchangeAccountRepository _repo;
            public FakeScope(IExchangeAccountRepository repo) => _repo = repo;
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) =>
                serviceType == typeof(IExchangeAccountRepository) ? _repo : null;
            public void Dispose() { }
        }
    }

    private sealed class FakeExchangeAccountRepository : IExchangeAccountRepository
    {
        private readonly List<ExchangeAccount> _store = new();

        public Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_store.FirstOrDefault(a => a.Id == id));

        public Task<ExchangeAccount?> GetActiveAsync(ExchangeName exchange, CancellationToken ct = default) =>
            Task.FromResult(_store.FirstOrDefault(a => a.Exchange == exchange && a.IsActive));

        public Task<IReadOnlyList<ExchangeAccount>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExchangeAccount>>(_store.ToList());

        public Task<IReadOnlyList<ExchangeAccount>> GetByExchangeAsync(
            ExchangeName exchange, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExchangeAccount>>(
                _store.Where(a => a.Exchange == exchange).ToList());

        public Task AddAsync(ExchangeAccount account, CancellationToken ct = default)
        {
            _store.Add(account);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ExchangeAccount account, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            _store.RemoveAll(a => a.Id == id);
            return Task.CompletedTask;
        }

        public Task SetActiveAsync(Guid id, CancellationToken ct = default)
        {
            var target = _store.First(a => a.Id == id);
            foreach (var sibling in _store.Where(a => a.Exchange == target.Exchange && a.Id != id && a.IsActive))
                sibling.Deactivate();
            target.Activate();
            return Task.CompletedTask;
        }
    }
}
