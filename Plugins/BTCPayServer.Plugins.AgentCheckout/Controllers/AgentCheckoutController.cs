#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.AgentCheckout.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [EnableCors(CorsPolicies.All)]
    public class AgentCheckoutController : ControllerBase
    {
        private readonly InvoiceRepository _invoiceRepository;
        private readonly Dictionary<PaymentMethodId, IPaymentLinkExtension> _paymentLinkExtensions;
        private readonly AgentCheckoutService _agentCheckoutService;

        public AgentCheckoutController(
            InvoiceRepository invoiceRepository,
            Dictionary<PaymentMethodId, IPaymentLinkExtension> paymentLinkExtensions,
            AgentCheckoutService agentCheckoutService)
        {
            _invoiceRepository = invoiceRepository;
            _paymentLinkExtensions = paymentLinkExtensions;
            _agentCheckoutService = agentCheckoutService;
        }

        [HttpGet("/stores/{storeId}/llms.txt")]
        public async Task<IActionResult> GetStoreLlmsTxt(string storeId)
        {
            var settings = await _agentCheckoutService.GetSettings(storeId);
            if (!settings.Enabled)
                return NotFound();

            var content = BuildLlmsTxt($"{Request.Scheme}://{Request.Host}");
            if (!string.IsNullOrWhiteSpace(settings.LlmsTxtAppend))
                content += "\n" + settings.LlmsTxtAppend.Trim() + "\n";

            return Content(content, "text/plain; charset=utf-8");
        }

        [HttpGet("/llms.txt")]
        public IActionResult GetLlmsTxt()
        {
            return Content(BuildLlmsTxt($"{Request.Scheme}://{Request.Host}"), "text/plain; charset=utf-8");
        }

        private string BuildLlmsTxt(string host)
        {
            return $@"# BTCPay Server Agent Checkout

> BTCPay Server lets LLM agents pay Bitcoin Lightning or on-chain invoices without authentication. Extract the invoiceId, request a quote, pay the returned URI, and poll for settlement.

Use these instructions only for invoices the user explicitly asked you to pay. Do not submit a payment without user intent and access to a wallet controlled by the user or agent.

Extract the `invoiceId` from a checkout URL like `{host}/i/{{invoiceId}}`.

Create a quote before paying:

```http
POST {host}/api/v1/agent/checkout/create-quote
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
  ""quoteExpiresAt"": ""2026-08-25T12:00:00Z""
}}
```

`quoteId` is a deterministic identifier for this (invoiceId, method) pair - safe to use as an idempotency key.
`paymentUrl` is the URI to pass to a compatible wallet. `btcAmount` is in satoshis.
`quoteExpiresAt` is when the invoice expires. Do not pay after this time.

After paying, check the invoice status:

```http
POST {host}/api/v1/agent/checkout/order-status
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
  ""paidAt"": null
}}
```

`paymentStatus` values: PENDING (not yet paid), PROCESSING (payment seen, confirming), PAID (settled), EXPIRED, INVALID, UNKNOWN (indeterminate - retry after a delay).
`deliveryStatus` is always UNKNOWN for payment-only flows; the merchant controls delivery confirmation.
If PENDING or PROCESSING, wait and check again. Treat UNKNOWN as a transient state and retry with backoff.

## Resources

- [Create Quote]({host}/api/v1/agent/checkout/create-quote): Request a Lightning or Bitcoin quote for an existing invoice.
- [Order Status]({host}/api/v1/agent/checkout/order-status): Check whether an invoice is paid, processing, or pending.
";
        }

        [HttpPost("/api/v1/agent/checkout/create-quote")]
        public async Task<IActionResult> CreateQuote([FromBody] CreateQuoteRequest? request)
        {
            if (string.IsNullOrEmpty(request?.InvoiceId))
                return BadRequest(new ErrorResponse("invoice-id-required", "invoiceId is required."));

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
                // auto: prefer Lightning, fall back to on-chain
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

            return Ok(new CreateQuoteResponse(
                quoteId,
                method,
                paymentUrl,
                btcAmountSats,
                "BTC",
                invoice.ExpirationTime));
        }

        [HttpPost("/api/v1/agent/checkout/order-status")]
        public async Task<IActionResult> GetOrderStatus([FromBody] OrderStatusRequest? request)
        {
            if (string.IsNullOrEmpty(request?.InvoiceId))
                return BadRequest(new ErrorResponse("invoice-id-required", "invoiceId is required."));

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

            return Ok(new OrderStatusResponse(
                request.InvoiceId,
                paymentStatus,
                "UNKNOWN",
                amountSats,
                paidAt));
        }

        private string ClientIp =>
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        private IActionResult RateLimited()
        {
            Response.Headers["Retry-After"] = "60";
            return StatusCode(429, new ErrorResponse("rate-limit-exceeded", "Too many requests. Try again later."));
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

        public CreateQuoteResponse(string quoteId, string method, string paymentUrl, long btcAmount, string currency, DateTimeOffset quoteExpiresAt)
        {
            QuoteId = quoteId;
            Method = method;
            PaymentUrl = paymentUrl;
            BtcAmount = btcAmount;
            Currency = currency;
            QuoteExpiresAt = quoteExpiresAt;
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

        public OrderStatusResponse(string invoiceId, string paymentStatus, string deliveryStatus, long amountSats, DateTimeOffset? paidAt)
        {
            InvoiceId = invoiceId;
            PaymentStatus = paymentStatus;
            DeliveryStatus = deliveryStatus;
            AmountSats = amountSats;
            PaidAt = paidAt;
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
