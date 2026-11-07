#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.AgentCheckout;
using BTCPayServer.Plugins.AgentCheckout.Controllers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using Xunit;

namespace BTCPayServer.Tests.AgentCheckout;

public class AgentCheckoutTests
{
    [Fact]
    public void NpubEncoding_MatchesNip19Vector()
    {
        const string pubKeyHex = "7e7e9c42a91bfef19fa929e5fda1b72e0ebc1a4c1141673e2794234d86addf4e";
        const string expectedNpub = "npub10elfcs4fr0l0r8af98jlmgdh9c8tcxjvz9qkw038js35mp4dma8qzvjptg";
        var actual = AgentCheckoutService.GetReceiptNpubFromHex(pubKeyHex);
        Assert.Equal(expectedNpub, actual);
    }

    [Fact]
    public void DefaultSettings_AreDisabled()
    {
        var settings = new AgentCheckoutSettings();
        Assert.False(settings.Enabled);
        Assert.False(settings.OptedIn);
        Assert.False(AgentCheckoutSettings.IsActive(settings));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void IsActive_RequiresBothEnabledAndOptedIn(bool enabled, bool optedIn, bool expected)
    {
        var settings = new AgentCheckoutSettings { Enabled = enabled, OptedIn = optedIn };
        Assert.Equal(expected, AgentCheckoutSettings.IsActive(settings));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("contains spaces here")]
    [InlineData("has/slash")]
    [InlineData("has..dotdot")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void IsValidStoreId_RejectsInvalidFormats(string id)
    {
        Assert.False(AgentCheckoutController.IsValidStoreId(id));
    }

    [Theory]
    [InlineData("ValidStoreId1234")]
    [InlineData("ABCD1234efgh5678")]
    public void IsValidStoreId_AcceptsValidFormats(string id)
    {
        Assert.True(AgentCheckoutController.IsValidStoreId(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has a space")]
    [InlineData("has/slash")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void IsValidInvoiceId_RejectsInvalidFormats(string id)
    {
        Assert.False(AgentCheckoutController.IsValidInvoiceId(id));
    }

    [Theory]
    [InlineData("W9ztGzD8L1YHC7ZqYmnRhs")]
    [InlineData("6XMipdLv8kEYGJmK9ZRVXq")]
    public void IsValidInvoiceId_AcceptsValidFormats(string id)
    {
        Assert.True(AgentCheckoutController.IsValidInvoiceId(id));
    }

    [Theory]
    [InlineData("192.168.1.1", "192.168.1.1")]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("unknown", "unknown")]
    public void GetRateLimitKey_IPv4_ReturnsIpAsIs(string ip, string expected)
    {
        Assert.Equal(expected, AgentCheckoutController.GetRateLimitKey(ip));
    }

    [Fact]
    public void Ipv6_SamePrefixSharesBucket()
    {
        var a = AgentCheckoutController.GetRateLimitKey("2001:db8:1:2:3:4:5:6");
        var b = AgentCheckoutController.GetRateLimitKey("2001:db8:1:2:ffff:ffff:ffff:ffff");
        Assert.Equal(a, b);
    }

    [Fact]
    public void GetRateLimitKey_IPv4MappedIPv6_UnwrapsToIPv4()
    {
        var mapped = AgentCheckoutController.GetRateLimitKey("::ffff:192.168.1.5");
        var plain = AgentCheckoutController.GetRateLimitKey("192.168.1.5");
        Assert.Equal(plain, mapped);
    }

    [Fact]
    public void GetRateLimitKey_DifferentIPv6Prefixes_GiveDifferentBuckets()
    {
        var a = AgentCheckoutController.GetRateLimitKey("2001:db8:1:2:3:4:5:6");
        var b = AgentCheckoutController.GetRateLimitKey("2001:db8:1:3:3:4:5:6");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task GetReceiptKey_NoKeyStored_ReturnsNullWithoutWrite()
    {
        var repo = new CountingStoreRepository();
        var service = new AgentCheckoutService(
            repo,
            new MemoryCache(new MemoryCacheOptions()),
            new AlwaysPresentChecker(),
            NullLogger<AgentCheckoutService>.Instance);
        var key = await service.GetReceiptKey("ABCD1234efgh5678");
        Assert.Null(key);
        Assert.Equal(0, repo.UpdateCount);
    }

    [Theory]
    [InlineData(InvoiceStatus.New, true)]
    [InlineData(InvoiceStatus.Processing, false)]
    [InlineData(InvoiceStatus.Settled, false)]
    [InlineData(InvoiceStatus.Expired, false)]
    [InlineData(InvoiceStatus.Invalid, false)]
    public void IsQuotable_OnlyTrueForNew(InvoiceStatus status, bool expected)
    {
        Assert.Equal(expected, AgentCheckoutController.IsQuotable(status));
    }

    [Theory]
    [InlineData(InvoiceStatus.Settled, true, null, true)]
    [InlineData(InvoiceStatus.Settled, true, true, true)]
    [InlineData(InvoiceStatus.Settled, true, false, false)]
    [InlineData(InvoiceStatus.Settled, false, null, false)]
    [InlineData(InvoiceStatus.Settled, null, null, false)]
    [InlineData(InvoiceStatus.Processing, true, null, false)]
    [InlineData(InvoiceStatus.New, true, null, false)]
    [InlineData(InvoiceStatus.Expired, true, null, false)]
    public void ShouldIncludePayments_OnlyTrueForSettledWithReceiptEnabled(
        InvoiceStatus status, bool? enabled, bool? showPayments, bool expected)
    {
        Assert.Equal(expected, AgentCheckoutController.ShouldIncludePayments(status, enabled, showPayments));
    }

    [Theory]
    [InlineData("hello world", "> hello world")]
    [InlineData("Receipt signing key: abc123\nhello", "> hello")]
    [InlineData("receipt signing key: xyz\ntest", "> test")]
    [InlineData("RECEIPT SIGNING KEY: abc\ntext", "> text")]
    [InlineData("  receipt  signing  key  : abc\ntest", "> test")]
    public void SanitizeMerchantText_FiltersAndFormats(string input, string expected)
    {
        Assert.Equal(expected, AgentCheckoutController.SanitizeMerchantText(input));
    }

    [Fact]
    public void SanitizeMerchantText_TruncatesLongInput()
    {
        var input = new string('a', 5000);
        var result = AgentCheckoutController.SanitizeMerchantText(input);
        Assert.True(result.Length <= 4002);
    }

    [Fact]
    public void SanitizeMerchantText_StripsControlChars()
    {
        var input = "hello\x01world";
        var result = AgentCheckoutController.SanitizeMerchantText(input);
        Assert.False(result.Contains('\x01'));
        Assert.Contains("hello", result);
        Assert.Contains("world", result);
    }

    [Fact]
    public void SanitizeMerchantText_PreservesMultipleLines()
    {
        var input = "line one\nline two";
        var result = AgentCheckoutController.SanitizeMerchantText(input);
        Assert.Equal("> line one\n> line two", result);
    }

    [Theory]
    [InlineData("LIGHTNING", "lightning:lnbctest", "hashval", null)]
    [InlineData("BITCOIN", null, null, "bc1qaddress")]
    public void BuildOfferTags_NeverContainsPreimage(
        string method, string? paymentUrl, string? paymentHash, string? address)
    {
        var tags = AgentCheckoutController.BuildOfferTags(
            "invoiceId", 1000L, 0.50m, "RON", method, paymentUrl, paymentHash, address,
            "quoteId", 9999999L, "storeId");
        Assert.DoesNotContain(tags, t => t.TagIdentifier == "preimage");
    }

    [Fact]
    public void BuildOfferTags_Lightning_ContainsBolt11AndPaymentHash()
    {
        var tags = AgentCheckoutController.BuildOfferTags(
            "inv1", 950700L, 0.50m, "RON", "LIGHTNING", "lightning:lnbctest", "hashval", null,
            "qid1", 1234567890L, "store1");
        Assert.Contains(tags, t => t.TagIdentifier == "bolt11" && t.Data[0] == "lnbctest");
        Assert.Contains(tags, t => t.TagIdentifier == "payment_hash" && t.Data[0] == "hashval");
        Assert.DoesNotContain(tags, t => t.TagIdentifier == "address");
    }

    [Fact]
    public void BuildOfferTags_Bitcoin_ContainsAddress()
    {
        var tags = AgentCheckoutController.BuildOfferTags(
            "inv1", 50000L, 0.50m, "RON", "BITCOIN", null, null, "bc1qaddress",
            "qid1", 1234567890L, "store1");
        Assert.Contains(tags, t => t.TagIdentifier == "address" && t.Data[0] == "bc1qaddress");
        Assert.DoesNotContain(tags, t => t.TagIdentifier == "bolt11");
        Assert.DoesNotContain(tags, t => t.TagIdentifier == "payment_hash");
    }

    [Fact]
    public void BuildReceiptTags_NeverContainsPreimage()
    {
        var tags = AgentCheckoutController.BuildReceiptTags(
            "inv1", null, "PAID", 950700L, 0.50m, "RON",
            new[] { "LIGHTNING" }, new[] { "hashval" },
            false, Array.Empty<string>(),
            1234567890L, false, null, 950700L, "store1");
        Assert.DoesNotContain(tags, t => t.TagIdentifier == "preimage");
    }

    [Fact]
    public void BuildReceiptTags_IncludesPaidAt_WhenRequested()
    {
        long paidAt = 1762480100L;
        var tags = AgentCheckoutController.BuildReceiptTags(
            "inv1", null, "PAID", 950700L, 0.50m, "RON",
            new[] { "LIGHTNING" }, new[] { "hashval" },
            false, Array.Empty<string>(),
            1234567890L, true, paidAt, 950700L, "store1");
        Assert.Contains(tags, t => t.TagIdentifier == "paid_at" && t.Data[0] == paidAt.ToString());
    }

    [Fact]
    public void BuildReceiptTags_OmitsPaidAt_WhenNotRequested()
    {
        var tags = AgentCheckoutController.BuildReceiptTags(
            "inv1", null, "PAID", 950700L, 0.50m, "RON",
            new[] { "LIGHTNING" }, new[] { "hashval" },
            false, Array.Empty<string>(),
            1234567890L, false, 1762480100L, 950700L, "store1");
        Assert.DoesNotContain(tags, t => t.TagIdentifier == "paid_at");
    }

    [Fact]
    public void BuildReceiptTags_OmitsTxid_WhenNotRequested()
    {
        var tags = AgentCheckoutController.BuildReceiptTags(
            "inv1", null, "PAID", 950700L, 0.50m, "RON",
            new[] { "BITCOIN" }, Array.Empty<string>(),
            false, new[] { "txid1234" },
            1234567890L, false, null, 950700L, "store1");
        Assert.DoesNotContain(tags, t => t.TagIdentifier == "txid");
    }

    [Fact]
    public void BuildReceiptTags_IncludesTxid_WhenRequested()
    {
        var tags = AgentCheckoutController.BuildReceiptTags(
            "inv1", null, "PAID", 950700L, 0.50m, "RON",
            new[] { "BITCOIN" }, Array.Empty<string>(),
            true, new[] { "txid1234" },
            1234567890L, false, null, 950700L, "store1");
        Assert.Contains(tags, t => t.TagIdentifier == "txid" && t.Data[0] == "txid1234");
    }

    [Theory]
    [InlineData("LIGHTNING", "lightning:lnbctest", "hashval", null)]
    [InlineData("BITCOIN", null, null, "bc1qaddress")]
    public async Task OfferEvent_SignatureIsValidAndNoPreimage(
        string method, string? paymentUrl, string? paymentHash, string? address)
    {
        var key = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var tags = AgentCheckoutController.BuildOfferTags(
            "invoiceId", 1000L, 0.50m, "RON", method, paymentUrl, paymentHash, address,
            "quoteId", 9999999L, "storeId");
        var evt = new NostrEvent { Kind = 1402, Content = "", Tags = tags };
        var signed = await evt.ComputeIdAndSignAsync(key);
        Assert.True(signed.Verify());
        Assert.DoesNotContain(signed.Tags, t => t.TagIdentifier == "preimage");
    }

    [Fact]
    public async Task ReceiptEvent_SignatureIsValidAndNoPreimage()
    {
        var key = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var tags = AgentCheckoutController.BuildReceiptTags(
            "invoiceId", null, "PAID", 1000L, 0.50m, "RON",
            new[] { "LIGHTNING" }, new[] { "hashval" },
            false, Array.Empty<string>(),
            9999999L, false, null, 1000L, "storeId");
        var evt = new NostrEvent { Kind = 31402, Content = "", Tags = tags };
        var signed = await evt.ComputeIdAndSignAsync(key);
        Assert.True(signed.Verify());
        Assert.DoesNotContain(signed.Tags, t => t.TagIdentifier == "preimage");
    }

    private sealed class StubStoreRepository : IStoreRepository
    {
        public Task<T?> GetSettingAsync<T>(string storeId, string name) where T : class =>
            Task.FromResult<T?>(null);
        public Task<Dictionary<string, T?>> GetSettingsAsync<T>(string name) where T : class =>
            Task.FromResult(new Dictionary<string, T?>());
        public Task UpdateSetting<T>(string storeId, string name, T obj) where T : class =>
            Task.CompletedTask;
    }

    private sealed class CountingStoreRepository : IStoreRepository
    {
        public int UpdateCount;
        public Task<T?> GetSettingAsync<T>(string storeId, string name) where T : class =>
            Task.FromResult<T?>(null);
        public Task<Dictionary<string, T?>> GetSettingsAsync<T>(string name) where T : class =>
            Task.FromResult(new Dictionary<string, T?>());
        public Task UpdateSetting<T>(string storeId, string name, T obj) where T : class
        {
            System.Threading.Interlocked.Increment(ref UpdateCount);
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysAbsentChecker : IStoreExistenceChecker
    {
        public Task<bool> ExistsAsync(string storeId) => Task.FromResult(false);
    }

    private sealed class AlwaysPresentChecker : IStoreExistenceChecker
    {
        public Task<bool> ExistsAsync(string storeId) => Task.FromResult(true);
    }
}
