#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Plugins.AgentCheckout;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using Xunit;

namespace BTCPayServer.Tests.AgentCheckout;

public class AgentCheckoutTests
{
    [Fact]
    public async Task Offer_Signature_IsValid()
    {
        var key = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var tags = new List<NostrEventTag>
        {
            new() { TagIdentifier = "invoice", Data = new() { "testInvoice" } },
            new() { TagIdentifier = "amount", Data = new() { "1000" } },
            new() { TagIdentifier = "store", Data = new() { "testStore" } },
        };
        var evt = new NostrEvent { Kind = 1402, Content = "", Tags = tags };
        var signed = await evt.ComputeIdAndSignAsync(key);
        Assert.True(signed.Verify());
        Assert.DoesNotContain(signed.Tags, t => t.TagIdentifier == "preimage");
    }

    [Fact]
    public async Task Receipt_Signature_IsValid()
    {
        var key = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var tags = new List<NostrEventTag>
        {
            new() { TagIdentifier = "d", Data = new() { "testInvoice" } },
            new() { TagIdentifier = "status", Data = new() { "PAID" } },
            new() { TagIdentifier = "store", Data = new() { "testStore" } },
        };
        var evt = new NostrEvent { Kind = 31402, Content = "", Tags = tags };
        var signed = await evt.ComputeIdAndSignAsync(key);
        Assert.True(signed.Verify());
        Assert.DoesNotContain(signed.Tags, t => t.TagIdentifier == "preimage");
    }

    [Fact]
    public void NpubEncoding_MatchesNip19Vector()
    {
        const string pubKeyHex = "7e7e9c42a91bfef19fa929e5fda1b72e0ebc1a4c1141673e2794234d86addf4e";
        const string expectedNpub = "npub10elfcs4fr0l0r8af98jlmgdh9c8tcxjvz9qkw038js35mp4dma8qzvjptg";
        var actual = AgentCheckoutService.GetReceiptNpubFromHex(pubKeyHex);
        Assert.Equal(expectedNpub, actual);
    }

    [Fact]
    public async Task Events_DoNotContainPreimage()
    {
        var key = ECPrivKey.Create(RandomUtils.GetBytes(32));

        var offerTags = new List<NostrEventTag>
        {
            new() { TagIdentifier = "payment_hash", Data = new() { "abc123" } }
        };
        var offer = new NostrEvent { Kind = 1402, Content = "", Tags = offerTags };
        var signedOffer = await offer.ComputeIdAndSignAsync(key);
        Assert.DoesNotContain(signedOffer.Tags, t => t.TagIdentifier == "preimage");

        var receiptTags = new List<NostrEventTag>
        {
            new() { TagIdentifier = "payment_hash", Data = new() { "abc123" } }
        };
        var receipt = new NostrEvent { Kind = 31402, Content = "", Tags = receiptTags };
        var signedReceipt = await receipt.ComputeIdAndSignAsync(key);
        Assert.DoesNotContain(signedReceipt.Tags, t => t.TagIdentifier == "preimage");
    }
}
