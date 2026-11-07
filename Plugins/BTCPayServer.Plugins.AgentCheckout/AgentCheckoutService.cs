#nullable enable
using System;
using System.Threading;
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

        // Limit per IP across all stores, checked before the invoice lookup so that
        // requests with unknown invoice IDs are throttled as well. Equal to the highest
        // value the settings page accepts, so the store limit still decides for real invoices.
        public const int GlobalRateLimitPerMinute = 300;

        public bool CheckGlobalRateLimit(string ip) =>
            CheckRateLimit("global", ip, GlobalRateLimitPerMinute);

        public bool CheckRateLimit(string storeId, string ip, int limitPerMinute)
        {
            if (limitPerMinute <= 0) return true;
            var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var key = $"ratelimit_{storeId}_{ip}_{minute}";
            // Atomic counter so concurrent requests cannot exceed the limit.
            var counter = _memoryCache.GetOrCreate(key, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                return new RateCounter();
            })!;
            return Interlocked.Increment(ref counter.Count) <= limitPerMinute;
        }

        private sealed class RateCounter
        {
            public int Count;
        }

        private static string CacheKey(string storeId) =>
            $"{nameof(AgentCheckoutSettings)}_{storeId}";
    }
}
