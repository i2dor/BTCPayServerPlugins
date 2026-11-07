#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.AgentCheckout
{
    public interface IStoreExistenceChecker
    {
        Task<bool> ExistsAsync(string storeId);
    }

    internal sealed class StoreRepositoryExistenceChecker : IStoreExistenceChecker
    {
        private readonly StoreRepository _storeRepo;
        private readonly IMemoryCache _cache;

        public StoreRepositoryExistenceChecker(StoreRepository storeRepo, IMemoryCache cache)
        {
            _storeRepo = storeRepo;
            _cache = cache;
        }

        public async Task<bool> ExistsAsync(string storeId)
        {
            var cacheKey = $"agco_store_exists_{storeId}";
            if (_cache.TryGetValue(cacheKey, out bool cached))
                return cached;
            var store = await _storeRepo.FindStore(storeId);
            var exists = store != null;
            if (exists)
                _cache.Set(cacheKey, true, TimeSpan.FromMinutes(5));
            return exists;
        }
    }
}
