#nullable enable
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.AgentCheckout
{
    public class AgentCheckoutService
    {
        private readonly IStoreRepository _storeRepository;
        private readonly IMemoryCache _memoryCache;

        public AgentCheckoutService(IStoreRepository storeRepository, IMemoryCache memoryCache)
        {
            _storeRepository = storeRepository;
            _memoryCache = memoryCache;
        }

        public async Task<AgentCheckoutSettings> GetSettings(string storeId)
        {
            var key = CacheKey(storeId);
            return await _memoryCache.GetOrCreateAsync(key, async _ =>
                await _storeRepository.GetSettingAsync<AgentCheckoutSettings>(storeId, nameof(AgentCheckoutSettings))
                ?? new AgentCheckoutSettings()) ?? new AgentCheckoutSettings();
        }

        public async Task UpdateSettings(string storeId, AgentCheckoutSettings settings)
        {
            await _storeRepository.UpdateSetting(storeId, nameof(AgentCheckoutSettings), settings);
            _memoryCache.Set(CacheKey(storeId), settings);
        }

        private static string CacheKey(string storeId) =>
            $"{nameof(AgentCheckoutSettings)}_{storeId}";
    }
}
