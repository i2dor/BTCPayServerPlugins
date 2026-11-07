#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;

namespace BTCPayServer.Plugins.AgentCheckout
{
    public class AgentCheckoutService
    {
        private readonly IStoreRepository _storeRepository;
        private readonly IMemoryCache _memoryCache;
        private readonly IStoreExistenceChecker _storeExistenceChecker;
        private readonly ILogger<AgentCheckoutService> _logger;

        public AgentCheckoutService(
            IStoreRepository storeRepository,
            IMemoryCache memoryCache,
            IStoreExistenceChecker storeExistenceChecker,
            ILogger<AgentCheckoutService> logger)
        {
            _storeRepository = storeRepository;
            _memoryCache = memoryCache;
            _storeExistenceChecker = storeExistenceChecker;
            _logger = logger;
        }

        public async Task<AgentCheckoutSettings> GetSettings(string storeId)
        {
            var cacheKey = CacheKey(storeId);
            if (_memoryCache.TryGetValue(cacheKey, out AgentCheckoutSettings? cached) && cached != null)
                return cached;
            if (!await _storeExistenceChecker.ExistsAsync(storeId))
                return new AgentCheckoutSettings { Enabled = false };
            return await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(10);
                return await _storeRepository.GetSettingAsync<AgentCheckoutSettings>(storeId, nameof(AgentCheckoutSettings))
                    ?? new AgentCheckoutSettings();
            }) ?? new AgentCheckoutSettings();
        }

        public async Task UpdateSettings(string storeId, AgentCheckoutSettings settings)
        {
            await _storeRepository.UpdateSetting(storeId, nameof(AgentCheckoutSettings), settings);
            _memoryCache.Set(CacheKey(storeId), settings, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(10)
            });
        }

        public async Task<ECPrivKey?> GetReceiptKey(string storeId)
        {
            if (!await _storeExistenceChecker.ExistsAsync(storeId))
                return null;
            var settings = await GetSettings(storeId);
            if (string.IsNullOrEmpty(settings.ReceiptPrivateKeyHex))
                return null;
            try { return NostrExtensions.ParseKey(settings.ReceiptPrivateKeyHex); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse receipt key for store {StoreId}", storeId);
                return null;
            }
        }

        public static string GetReceiptPubKeyHex(ECPrivKey key) =>
            key.CreateXOnlyPubKey().ToHex();

        public static string GetReceiptNpub(ECPrivKey key)
        {
            var pubKeyBytes = key.CreateXOnlyPubKey().ToBytes();
            return ToBech32Nostr("npub", pubKeyBytes);
        }

        public static string GetReceiptNpubFromHex(string hexPubKey)
        {
            var pubKeyBytes = Convert.FromHexString(hexPubKey);
            return ToBech32Nostr("npub", pubKeyBytes);
        }

        private static string ToBech32Nostr(string hrp, byte[] data)
        {
            const string CHARSET = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
            var values = new List<byte>();
            int acc = 0, bits = 0;
            foreach (var b in data)
            {
                acc = ((acc << 8) | b) & 0x1FFFF;
                bits += 8;
                while (bits >= 5) { bits -= 5; values.Add((byte)((acc >> bits) & 31)); }
            }
            if (bits > 0) values.Add((byte)((acc << (5 - bits)) & 31));
            var checkInput = new List<byte>();
            foreach (char c in hrp) checkInput.Add((byte)(c >> 5));
            checkInput.Add(0);
            foreach (char c in hrp) checkInput.Add((byte)(c & 31));
            checkInput.AddRange(values);
            checkInput.AddRange(new byte[6]);
            uint chk = Bech32Polymod(checkInput) ^ 1;
            for (int i = 0; i < 6; i++) values.Add((byte)((chk >> (5 * (5 - i))) & 31));
            return hrp + "1" + new string(values.Select(v => CHARSET[v]).ToArray());
        }

        private static uint Bech32Polymod(List<byte> values)
        {
            uint[] gen = { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };
            uint chk = 1;
            foreach (var v in values)
            {
                uint b = chk >> 25;
                chk = (chk & 0x1FFFFFF) << 5 ^ v;
                for (int i = 0; i < 5; i++)
                    if (((b >> i) & 1) != 0) chk ^= gen[i];
            }
            return chk;
        }

        public const int GlobalRateLimitPerMinute = 300;

        public bool CheckGlobalRateLimit(string ip) =>
            CheckRateLimit("global", ip, GlobalRateLimitPerMinute);

        public bool CheckRateLimit(string storeId, string ip, int limitPerMinute)
        {
            if (limitPerMinute <= 0) return true;
            var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var key = $"ratelimit_{storeId}_{ip}_{minute}";
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
