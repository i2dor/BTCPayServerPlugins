#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NNostr.Client;
using NNostr.Client.Protocols;

namespace BTCPayServer.Plugins.AgentCheckout.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [EnableCors(CorsPolicies.All)]
    [TypeFilter(typeof(AgentCheckoutExceptionFilter))]
    public class AgentCheckoutController : ControllerBase
    {
        private readonly InvoiceRepository _invoiceRepository;
        private readonly Dictionary<PaymentMethodId, IPaymentLinkExtension> _paymentLinkExtensions;
        private readonly AgentCheckoutService _agentCheckoutService;
        private readonly PaymentMethodHandlerDictionary _handlers;
        private readonly IMemoryCache _memoryCache;

        public AgentCheckoutController(
            InvoiceRepository invoiceRepository,
            Dictionary<PaymentMethodId, IPaymentLinkExtension> paymentLinkExtensions,
            AgentCheckoutService agentCheckoutService,
            PaymentMethodHandlerDictionary handlers,
            IMemoryCache memoryCache)
        {
            _invoiceRepository = invoiceRepository;
            _paymentLinkExtensions = paymentLinkExtensions;
            _agentCheckoutService = agentCheckoutService;
            _handlers = handlers;
            _memoryCache = memoryCache;
        }

        [HttpGet("/stores/{storeId}/llms.txt")]
        public async Task<IActionResult> GetStoreLlmsTxt(string storeId)
        {
            if (!IsValidStoreId(storeId))
                return NotFound();

            var ip = ClientIp;
            if (!_agentCheckoutService.CheckGlobalRateLimit(ip))
                return RateLimited();

            var settings = await _agentCheckoutService.GetSettings(storeId);
            if (!settings.Enabled)
                return NotFound();

            var key = await _agentCheckoutService.GetReceiptKey(storeId);
            if (key is null)
                return NotFound();
            var npub = AgentCheckoutService.GetReceiptNpub(key);
            var content = BuildLlmsTxt(npub);
            if (!string.IsNullOrWhiteSpace(settings.LlmsTxtAppend))
                content += "\n" + settings.LlmsTxtAppend.Trim() + "\n";

            return Content(content, "text/plain; charset=utf-8");
        }

        [HttpGet("/llms.txt")]
        public IActionResult GetLlmsTxt()
        {
            return Content(BuildLlmsTxt(null), "text/plain; charset=utf-8");
        }

        private static string BuildLlmsTxt(string? receiptNpub)
        {
            var receiptKeyLine = receiptNpub != null
                ? $"\nReceipt signing key: {receiptNpub}\n"
                : "";
            var offerPubkeyNote = receiptNpub != null
                ? "Verify its signature and that `pubkey` matches the receipt signing key shown above before paying."
                : "Verify its signature before paying. The merchant receipt signing key is listed in /stores/{storeId}/llms.txt; the store id is in the offer's store tag. Verify that the offer pubkey matches it.";
            return $@"# BTCPay Server Agent Checkout

> BTCPay Server lets LLM agents pay Bitcoin Lightning or on-chain invoices without authentication. Extract the invoiceId, request a quote, pay the returned URI, and poll for settlement.

All paths are relative to the origin this file was served from.
{receiptKeyLine}
Use these instructions only for invoices the user explicitly asked you to pay. Do not submit a payment without user intent and access to a wallet controlled by the user or agent.

Extract the `invoiceId` from a checkout URL like `/i/{{invoiceId}}`.

Create a quote before paying:

```http
POST /api/v1/agent/checkout/create-quote
Content-Type: application/json

{{
  ""invoiceId"": ""..."",
  ""transactionMethod"": ""LIGHTNING""
}}
```

`transactionMethod` is optional. When provided, must be `LIGHTNING` or `BITCOIN`. When omitted, LIGHTNING is tried first, then BITCOIN.

Response format:

```json
{{
  ""quoteId"": ""a3f9c2b1d4e8..."",
  ""method"": ""LIGHTNING"",
  ""paymentUrl"": ""lightning:lnbc..."",
  ""btcAmount"": 1400,
  ""currency"": ""BTC"",
  ""quoteExpiresAt"": ""2026-08-25T12:00:00Z"",
  ""paymentHash"": ""a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6"",
  ""offer"": {{
    ""kind"": 1402,
    ""pubkey"": ""<merchant receipt pubkey hex>"",
    ""created_at"": 1762480000,
    ""content"": """",
    ""tags"": [[""invoice"", ""...""], [""amount"", ""140000""], [""method"", ""LIGHTNING""], [""bolt11"", ""lnbc...""], [""payment_hash"", ""...""], [""quote"", ""...""], [""valid_until"", ""...""], [""store"", ""...""]],
    ""id"": ""<event id>"",
    ""sig"": ""<schnorr sig>""
  }}
}}
```

`quoteId` is a deterministic identifier for this (invoiceId, method) pair - safe to use as an idempotency key.
`paymentUrl` is the URI to pass to a compatible wallet. `btcAmount` is in satoshis.
`quoteExpiresAt` is when the invoice expires. Do not pay after this time.
`paymentHash` is the Lightning payment hash (hex) when the method is LIGHTNING, otherwise null. Your wallet returns the matching preimage after paying; keep it as proof of payment.
`offer` is a Nostr kind 1402 event signed by the merchant. {offerPubkeyNote}

To verify the offer: check that `sig` is a valid Schnorr signature over `id`, which is the SHA256 of the serialized event. Verify that `valid_until` is in the future. For Lightning, verify that `bolt11` decodes to the same `payment_hash`.

After paying, check the invoice status:

```http
POST /api/v1/agent/checkout/order-status
Content-Type: application/json

{{
  ""invoiceId"": ""...""
}}
```

Status response:

```json
{{
  ""invoiceId"": ""..."",
  ""paymentStatus"": ""PROCESSING"",
  ""deliveryStatus"": ""UNKNOWN"",
  ""amountSats"": 1400,
  ""paidAt"": null,
  ""paymentHash"": ""a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6""
}}
```

`paymentStatus` values: PENDING (not yet paid), PROCESSING (payment seen, confirming), PAID (settled), EXPIRED, INVALID, UNKNOWN (indeterminate - retry after a delay).
`deliveryStatus` is always UNKNOWN for payment-only flows; the merchant controls delivery confirmation.
If PENDING or PROCESSING, wait and check again. Treat UNKNOWN as a transient state and retry with backoff.

## Resources

- [Create Quote](/api/v1/agent/checkout/create-quote): Request a Lightning or Bitcoin quote for an existing invoice.
- [Order Status](/api/v1/agent/checkout/order-status): Check whether an invoice is paid, processing, or pending.
- [Receipt](/api/v1/agent/checkout/receipt): Retrieve a signed Nostr receipt for an invoice.
";
        }

        [HttpPost("/api/v1/agent/checkout/create-quote")]
        [RequestSizeLimit(4096)]
        public async Task<IActionResult> CreateQuote([FromBody] CreateQuoteRequest? request)
        {
            if (string.IsNullOrEmpty(request?.InvoiceId))
                return BadRequest(new ErrorResponse("invoice-id-required", "invoiceId is required."));

            if (!IsValidInvoiceId(request.InvoiceId))
                return BadRequest(new ErrorResponse("invoice-id-invalid", "invoiceId format is invalid."));

            var ip = ClientIp;
            if (!_agentCheckoutService.CheckGlobalRateLimit(ip))
                return RateLimited();

            var invoice = await _invoiceRepository.GetInvoice(request.InvoiceId);
            if (invoice is null)
                return NotFound(new ErrorResponse("invoice-not-found", "The invoice was not found."));

            var settings = await _agentCheckoutService.GetSettings(invoice.StoreId);
            if (!settings.Enabled)
                return StatusCode(503, new ErrorResponse("agent-checkout-disabled", "Agent Checkout is not enabled for this store."));

            if (!_agentCheckoutService.CheckRateLimit(invoice.StoreId, ip, settings.RateLimitPerMinute))
                return RateLimited();

            if (invoice.Status == InvoiceStatus.Expired || invoice.Status == InvoiceStatus.Invalid)
                return BadRequest(new ErrorResponse("invoice-expired", "The invoice is expired or invalid."));

            if (invoice.Status == InvoiceStatus.Settled)
                return BadRequest(new ErrorResponse("invoice-already-paid", "The invoice has already been paid."));

            var prompts = invoice.GetPaymentPrompts();

            var lnPmId = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var btcPmId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");

            PaymentPrompt? selectedPrompt = null;
            string method;

            var requestedMethod = request.TransactionMethod?.ToUpperInvariant();
            if (requestedMethod == "BITCOIN")
            {
                if (!settings.AllowOnChain)
                    return BadRequest(new ErrorResponse("payment-method-disabled", "On-chain Bitcoin payments are not allowed by this store."));
                prompts.TryGetValue(btcPmId, out selectedPrompt);
                method = "BITCOIN";
            }
            else if (requestedMethod == "LIGHTNING")
            {
                if (!settings.AllowLightning)
                    return BadRequest(new ErrorResponse("payment-method-disabled", "Lightning payments are not allowed by this store."));
                prompts.TryGetValue(lnPmId, out selectedPrompt);
                method = "LIGHTNING";
            }
            else
            {
                if (settings.AllowLightning && prompts.TryGetValue(lnPmId, out selectedPrompt) && selectedPrompt?.Activated == true)
                    method = "LIGHTNING";
                else if (settings.AllowOnChain && prompts.TryGetValue(btcPmId, out selectedPrompt) && selectedPrompt?.Activated == true)
                    method = "BITCOIN";
                else
                    return BadRequest(new ErrorResponse("no-payment-method", "No activated payment method is available or permitted for this invoice."));
            }

            if (selectedPrompt is null || !selectedPrompt.Activated)
                return BadRequest(new ErrorResponse("payment-method-inactive", $"The {method} payment method is not activated for this invoice."));

            if (!_paymentLinkExtensions.TryGetValue(selectedPrompt.PaymentMethodId, out var linkExtension))
                return BadRequest(new ErrorResponse("no-payment-link-extension", "Payment link extension not found for this payment method."));

            var paymentUrl = linkExtension.GetPaymentLink(selectedPrompt, Url);
            if (string.IsNullOrEmpty(paymentUrl))
                return BadRequest(new ErrorResponse("payment-link-unavailable", "Payment link could not be generated. The invoice may not be activated yet."));

            var accounting = selectedPrompt.Calculate();
            var btcAmountSats = (long)(accounting.Due * 100_000_000m);
            var quoteId = ComputeQuoteId(request.InvoiceId, method);
            var paymentHash = method == "LIGHTNING" ? GetLightningPaymentHash(invoice) : null;

            var receiptKey = await _agentCheckoutService.GetReceiptKey(invoice.StoreId);
            var amountMsatForKey = (long)(accounting.Due * 100_000_000_000m);
            NostrEvent? signedOffer = null;
            if (receiptKey is not null)
            {
                var cacheKey = $"agco_offer_{request.InvoiceId}_{method}_{selectedPrompt.Destination}_{amountMsatForKey}";
                signedOffer = await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
                {
                    entry.AbsoluteExpiration = invoice.ExpirationTime;
                    var amountMsat = (long)(accounting.Due * 100_000_000_000m);
                    var tags = new List<NostrEventTag>
                    {
                        new() { TagIdentifier = "invoice", Data = new() { request.InvoiceId } },
                        new() { TagIdentifier = "amount", Data = new() { amountMsat.ToString() } },
                        new() { TagIdentifier = "price", Data = new() { invoice.Price.ToString(CultureInfo.InvariantCulture), invoice.Currency } },
                        new() { TagIdentifier = "method", Data = new() { method } }
                    };
                    if (method == "LIGHTNING" && !string.IsNullOrEmpty(paymentUrl))
                    {
                        var bolt11 = paymentUrl.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase)
                            ? paymentUrl[10..]
                            : paymentUrl;
                        tags.Add(new() { TagIdentifier = "bolt11", Data = new() { bolt11 } });
                        if (paymentHash != null)
                            tags.Add(new() { TagIdentifier = "payment_hash", Data = new() { paymentHash } });
                    }
                    else if (method == "BITCOIN" && !string.IsNullOrEmpty(selectedPrompt.Destination))
                    {
                        tags.Add(new() { TagIdentifier = "address", Data = new() { selectedPrompt.Destination } });
                    }
                    tags.Add(new() { TagIdentifier = "quote", Data = new() { quoteId } });
                    tags.Add(new() { TagIdentifier = "valid_until", Data = new() { invoice.ExpirationTime.ToUnixTimeSeconds().ToString() } });
                    tags.Add(new() { TagIdentifier = "store", Data = new() { invoice.StoreId } });
                    var evt = new NostrEvent { Kind = 1402, Content = "", Tags = tags };
                    return await evt.ComputeIdAndSignAsync(receiptKey!);
                });
            }

            var offerIdsKey = $"agco_offer_ids_{request.InvoiceId}";
            var offerIds = _memoryCache.GetOrCreate(offerIdsKey, entry =>
            {
                entry.AbsoluteExpiration = invoice.ExpirationTime.AddDays(7);
                return new List<string>();
            })!;
            if (signedOffer?.Id != null)
                lock (offerIds) { if (!offerIds.Contains(signedOffer.Id)) offerIds.Add(signedOffer.Id); }

            JObject? offerJObject = null;
            if (signedOffer != null)
            {
                var offerStr = System.Text.Json.JsonSerializer.Serialize(signedOffer);
                offerJObject = JObject.Parse(offerStr);
            }

            return Ok(new CreateQuoteResponse(
                quoteId,
                method,
                paymentUrl,
                btcAmountSats,
                "BTC",
                invoice.ExpirationTime,
                paymentHash,
                offerJObject));
        }

        [HttpPost("/api/v1/agent/checkout/order-status")]
        [RequestSizeLimit(4096)]
        public async Task<IActionResult> GetOrderStatus([FromBody] OrderStatusRequest? request)
        {
            if (string.IsNullOrEmpty(request?.InvoiceId))
                return BadRequest(new ErrorResponse("invoice-id-required", "invoiceId is required."));

            if (!IsValidInvoiceId(request.InvoiceId))
                return BadRequest(new ErrorResponse("invoice-id-invalid", "invoiceId format is invalid."));

            var ip = ClientIp;
            if (!_agentCheckoutService.CheckGlobalRateLimit(ip))
                return RateLimited();

            var invoice = await _invoiceRepository.GetInvoice(request.InvoiceId);
            if (invoice is null)
                return NotFound(new ErrorResponse("invoice-not-found", "The invoice was not found."));

            var settings = await _agentCheckoutService.GetSettings(invoice.StoreId);
            if (!settings.Enabled)
                return StatusCode(503, new ErrorResponse("agent-checkout-disabled", "Agent Checkout is not enabled for this store."));

            if (!_agentCheckoutService.CheckRateLimit(invoice.StoreId, ip, settings.RateLimitPerMinute))
                return RateLimited();

            var paymentStatus = invoice.Status switch
            {
                InvoiceStatus.New => "PENDING",
                InvoiceStatus.Processing => "PROCESSING",
                InvoiceStatus.Settled => "PAID",
                InvoiceStatus.Expired => "EXPIRED",
                InvoiceStatus.Invalid => "INVALID",
                _ => "UNKNOWN"
            };

            DateTimeOffset? paidAt = null;
            if (invoice.Status is InvoiceStatus.Settled or InvoiceStatus.Processing)
            {
                var payments = invoice.GetPayments(false);
                foreach (var p in payments)
                {
                    if (paidAt is null || p.ReceivedTime > paidAt)
                        paidAt = p.ReceivedTime;
                }
            }

            var amountSats = (long)(invoice.Price * 100_000_000m);
            var paymentHash = GetLightningPaymentHash(invoice);

            return Ok(new OrderStatusResponse(
                request.InvoiceId,
                paymentStatus,
                "UNKNOWN",
                amountSats,
                paidAt,
                paymentHash));
        }

        [HttpPost("/api/v1/agent/checkout/receipt")]
        [RequestSizeLimit(4096)]
        public async Task<IActionResult> GetReceipt([FromBody] ReceiptRequest? request)
        {
            if (string.IsNullOrEmpty(request?.InvoiceId))
                return BadRequest(new ErrorResponse("invoice-id-required", "invoiceId is required."));

            if (!IsValidInvoiceId(request.InvoiceId))
                return BadRequest(new ErrorResponse("invoice-id-invalid", "invoiceId format is invalid."));

            var ip = ClientIp;
            if (!_agentCheckoutService.CheckGlobalRateLimit(ip))
                return RateLimited();

            var invoice = await _invoiceRepository.GetInvoice(request.InvoiceId);
            if (invoice is null)
                return NotFound(new ErrorResponse("invoice-not-found", "The invoice was not found."));

            var settings = await _agentCheckoutService.GetSettings(invoice.StoreId);
            if (!settings.Enabled)
                return StatusCode(503, new ErrorResponse("agent-checkout-disabled", "Agent Checkout is not enabled for this store."));

            if (!_agentCheckoutService.CheckRateLimit(invoice.StoreId, ip, settings.RateLimitPerMinute))
                return RateLimited();

            var lnPmId = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var btcPmId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
            var prompts = invoice.GetPaymentPrompts();

            _handlers.TryGetValue(lnPmId, out var lnH);
            var payments = invoice.GetPayments(false).Where(p => p.Accounted).ToList();
            var paidMethods = new HashSet<string>();
            var paymentHashes = new HashSet<string>();
            var txids = new HashSet<string>();
            DateTimeOffset? paidAt = null;
            foreach (var p in payments)
            {
                if (p.PaymentMethodId == lnPmId)
                {
                    paidMethods.Add("LIGHTNING");
                    if (lnH != null)
                    {
                        var lnData = p.GetDetails<LightningLikePaymentData>(lnH);
                        if (lnData?.PaymentHash != null)
                            paymentHashes.Add(lnData.PaymentHash.ToString());
                    }
                }
                else if (p.PaymentMethodId == btcPmId)
                {
                    paidMethods.Add("BITCOIN");
                    if (_handlers.TryGetValue(btcPmId, out var btcH))
                    {
                        var btcData = p.GetDetails<BitcoinLikePaymentData>(btcH);
                        if (btcData?.Outpoint?.Hash != null)
                            txids.Add(btcData.Outpoint.Hash.ToString());
                    }
                }
                if (paidAt is null || p.ReceivedTime > paidAt)
                    paidAt = p.ReceivedTime;
            }

            PaymentPrompt? amountPrompt = null;
            if (paidMethods.Contains("LIGHTNING") && prompts.TryGetValue(lnPmId, out var lnP))
                amountPrompt = lnP;
            else if (paidMethods.Contains("BITCOIN") && prompts.TryGetValue(btcPmId, out var btcP))
                amountPrompt = btcP;
            else if (prompts.TryGetValue(lnPmId, out var lnPDef) && lnPDef?.Activated == true)
                amountPrompt = lnPDef;
            else if (prompts.TryGetValue(btcPmId, out var btcPDef) && btcPDef?.Activated == true)
                amountPrompt = btcPDef;

            long amountMsat = 0;
            long paidMsat = 0;
            if (amountPrompt != null)
            {
                var acc = amountPrompt.Calculate();
                amountMsat = (long)(acc.TotalDue * 100_000_000_000m);
                paidMsat = (long)(acc.Paid * 100_000_000_000m);
            }

            var receiptKey = await _agentCheckoutService.GetReceiptKey(invoice.StoreId);
            if (receiptKey is null)
                return StatusCode(503, new ErrorResponse("receipt-key-unavailable", "Receipt signing key is not available."));
            var pubKeyHex = AgentCheckoutService.GetReceiptPubKeyHex(receiptKey);
            var npub = AgentCheckoutService.GetReceiptNpub(receiptKey);

            var offerIdsKey = $"agco_offer_ids_{request.InvoiceId}";
            _memoryCache.TryGetValue<List<string>>(offerIdsKey, out var offerIds);

            var paymentStatus = invoice.Status switch
            {
                InvoiceStatus.New => "PENDING",
                InvoiceStatus.Processing => "PROCESSING",
                InvoiceStatus.Settled => "PAID",
                InvoiceStatus.Expired => "EXPIRED",
                InvoiceStatus.Invalid => "INVALID",
                _ => "UNKNOWN"
            };

            var tags = new List<NostrEventTag>();
            tags.Add(new() { TagIdentifier = "d", Data = new() { request.InvoiceId } });
            if (offerIds != null)
                lock (offerIds) { foreach (var id in offerIds) tags.Add(new() { TagIdentifier = "e", Data = new() { id } }); }
            tags.Add(new() { TagIdentifier = "status", Data = new() { paymentStatus } });
            tags.Add(new() { TagIdentifier = "delivery", Data = new() { "UNKNOWN" } });
            tags.Add(new() { TagIdentifier = "amount", Data = new() { amountMsat.ToString() } });
            tags.Add(new() { TagIdentifier = "price", Data = new() { invoice.Price.ToString(CultureInfo.InvariantCulture), invoice.Currency } });
            foreach (var m in paidMethods) tags.Add(new() { TagIdentifier = "method", Data = new() { m } });
            foreach (var ph in paymentHashes) tags.Add(new() { TagIdentifier = "payment_hash", Data = new() { ph } });
            foreach (var txid in txids) tags.Add(new() { TagIdentifier = "txid", Data = new() { txid } });
            foreach (var m in paidMethods) tags.Add(new() { TagIdentifier = "quote", Data = new() { ComputeQuoteId(request.InvoiceId, m) } });
            tags.Add(new() { TagIdentifier = "quote_expires_at", Data = new() { invoice.ExpirationTime.ToUnixTimeSeconds().ToString() } });
            if (paidAt.HasValue) tags.Add(new() { TagIdentifier = "paid_at", Data = new() { paidAt.Value.ToUnixTimeSeconds().ToString() } });
            if (paidMsat > 0 && paidMsat != amountMsat) tags.Add(new() { TagIdentifier = "paid", Data = new() { paidMsat.ToString() } });
            tags.Add(new() { TagIdentifier = "store", Data = new() { invoice.StoreId } });

            var receiptEvt = new NostrEvent { Kind = 31402, Content = "", Tags = tags };
            var signedReceipt = await receiptEvt.ComputeIdAndSignAsync(receiptKey);

            var receiptStr = System.Text.Json.JsonSerializer.Serialize(signedReceipt);
            var receiptJObject = JObject.Parse(receiptStr);

            return Ok(new ReceiptResponse(receiptJObject, pubKeyHex, npub));
        }

        private string ClientIp =>
            GetRateLimitKey(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        internal static string GetRateLimitKey(string ip)
        {
            if (IPAddress.TryParse(ip, out var addr) &&
                addr.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = addr.GetAddressBytes();
                for (int i = 8; i < 16; i++) bytes[i] = 0;
                return new IPAddress(bytes) + "/64";
            }
            return ip;
        }

        internal static bool IsValidStoreId(string id) =>
            id.Length >= 10 && id.Length <= 64 &&
            id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

        internal static bool IsValidInvoiceId(string id) =>
            id.Length >= 10 && id.Length <= 64 &&
            id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

        private IActionResult RateLimited()
        {
            Response.Headers["Retry-After"] = "60";
            return StatusCode(429, new ErrorResponse("rate-limit-exceeded", "Too many requests. Try again later."));
        }

        private string? GetLightningPaymentHash(InvoiceEntity invoice)
        {
            var lnPmId = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var prompts = invoice.GetPaymentPrompts();
            if (!prompts.TryGetValue(lnPmId, out var prompt) || prompt?.Activated != true)
                return null;
            if (!_handlers.TryGetValue(lnPmId, out var handler))
                return null;
            var details = handler.ParsePaymentPromptDetails(prompt.Details) as LigthningPaymentPromptDetails;
            return details?.PaymentHash?.ToString();
        }

        private static string ComputeQuoteId(string invoiceId, string method)
        {
            var input = Encoding.UTF8.GetBytes($"{invoiceId}:{method}");
            var hash = SHA256.HashData(input);
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
    }

    public class CreateQuoteRequest
    {
        [JsonProperty("invoiceId")]
        public string? InvoiceId { get; set; }

        [JsonProperty("transactionMethod")]
        public string? TransactionMethod { get; set; }
    }

    public class CreateQuoteResponse
    {
        [JsonProperty("quoteId")]
        public string QuoteId { get; }

        [JsonProperty("method")]
        public string Method { get; }

        [JsonProperty("paymentUrl")]
        public string PaymentUrl { get; }

        [JsonProperty("btcAmount")]
        public long BtcAmount { get; }

        [JsonProperty("currency")]
        public string Currency { get; }

        [JsonProperty("quoteExpiresAt")]
        public DateTimeOffset QuoteExpiresAt { get; }

        [JsonProperty("paymentHash")]
        public string? PaymentHash { get; }

        [JsonProperty("offer")]
        public JObject? Offer { get; }

        public CreateQuoteResponse(string quoteId, string method, string paymentUrl, long btcAmount, string currency, DateTimeOffset quoteExpiresAt, string? paymentHash, JObject? offer)
        {
            QuoteId = quoteId;
            Method = method;
            PaymentUrl = paymentUrl;
            BtcAmount = btcAmount;
            Currency = currency;
            QuoteExpiresAt = quoteExpiresAt;
            PaymentHash = paymentHash;
            Offer = offer;
        }
    }

    public class OrderStatusRequest
    {
        [JsonProperty("invoiceId")]
        public string? InvoiceId { get; set; }
    }

    public class OrderStatusResponse
    {
        [JsonProperty("invoiceId")]
        public string InvoiceId { get; }

        [JsonProperty("paymentStatus")]
        public string PaymentStatus { get; }

        [JsonProperty("deliveryStatus")]
        public string DeliveryStatus { get; }

        [JsonProperty("amountSats")]
        public long AmountSats { get; }

        [JsonProperty("paidAt")]
        public DateTimeOffset? PaidAt { get; }

        [JsonProperty("paymentHash")]
        public string? PaymentHash { get; }

        public OrderStatusResponse(string invoiceId, string paymentStatus, string deliveryStatus, long amountSats, DateTimeOffset? paidAt, string? paymentHash)
        {
            InvoiceId = invoiceId;
            PaymentStatus = paymentStatus;
            DeliveryStatus = deliveryStatus;
            AmountSats = amountSats;
            PaidAt = paidAt;
            PaymentHash = paymentHash;
        }
    }

    public class ReceiptRequest
    {
        [JsonProperty("invoiceId")]
        public string? InvoiceId { get; set; }
    }

    public class ReceiptResponse
    {
        [JsonProperty("receipt")]
        public JObject Receipt { get; }

        [JsonProperty("pubkey")]
        public string PubKey { get; }

        [JsonProperty("npub")]
        public string Npub { get; }

        public ReceiptResponse(JObject receipt, string pubKey, string npub)
        {
            Receipt = receipt;
            PubKey = pubKey;
            Npub = npub;
        }
    }

    public class ErrorResponse
    {
        [JsonProperty("code")]
        public string Code { get; }

        [JsonProperty("message")]
        public string Message { get; }

        public ErrorResponse(string code, string message)
        {
            Code = code;
            Message = message;
        }
    }
}
