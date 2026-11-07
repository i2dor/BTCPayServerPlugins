# BTCPay Server - Agent Checkout Plugin

Exposes an LLM-readable `/llms.txt` and a public quote API so AI agents can pay BTCPay invoices via Lightning or on-chain Bitcoin without authentication.

## How it works

1. An AI agent reads `/llms.txt` to discover the payment API
2. The agent calls `POST /api/v1/agent/checkout/create-quote` with an `invoiceId`
3. BTCPay returns a payment URI (BOLT11 or `bitcoin:` address) and the amount in satoshis
4. The agent pays and polls `POST /api/v1/agent/checkout/order-status` until the invoice is settled

---

## Installation

1. Download the latest `.btcpay` file from [GitHub Actions artifacts](https://github.com/i2dor/BTCPayServerPlugins/actions)
2. In BTCPay Server: **Server Settings -> Plugins -> Upload Plugin**
3. Restart BTCPay Server when prompted
4. Go to **Store Settings -> Integrations -> Agent Checkout** to configure each store

**Requires:** BTCPay Server >= 2.3.7

---

## Store Settings

Navigate to **Store Settings -> Integrations -> Agent Checkout**.

| Setting | Default | Description |
|---|---|---|
| Enable Agent Checkout | On | Master toggle. When off, all endpoints return 503 for invoices from this store. |
| Allow Lightning payments | On | Whether agents can request a BOLT11 Lightning invoice. |
| Allow on-chain BTC | On | Whether agents can request an on-chain Bitcoin address. |
| Rate limit (req/min per IP) | 30 | Max calls per IP per minute to the quote and status endpoints. Returns 429 when exceeded. |
| Custom llms.txt content | empty | Markdown appended to the per-store `/stores/{storeId}/llms.txt`. Use this to add store-specific instructions for agents. |

---

## Endpoints

All endpoints are public (no authentication required) and CORS-enabled.

### `GET /llms.txt`

Returns global machine-readable payment instructions for any AI agent visiting this BTCPay instance. Contains API documentation, request/response shapes, and status values.

```
GET https://btcpay.example.com/llms.txt
```

### `GET /stores/{storeId}/llms.txt`

Same as above but with any custom content from this store's **Custom llms.txt content** setting appended. Returns 404 if Agent Checkout is disabled for this store.

```
GET https://btcpay.example.com/stores/HAucTGdnoKwfNjy7iySQSNp2wpU6rvPbDU3KanLuhJFD/llms.txt
```

### `POST /api/v1/agent/checkout/create-quote`

Returns a payment URI and amount for an existing invoice.

**Request:**

```json
{
  "invoiceId": "W9ztGzD8L1YHC7ZqYmnRhs",
  "transactionMethod": "LIGHTNING"
}
```

| Field | Required | Values | Description |
|---|---|---|---|
| `invoiceId` | Yes | string | BTCPay invoice ID (from the checkout URL `/i/{invoiceId}`) |
| `transactionMethod` | No | `LIGHTNING`, `BITCOIN` | Preferred payment method. When omitted: Lightning is tried first, then on-chain. |

**Response `200`:**

```json
{
  "quoteId": "a3f9c2b1d4e87f20",
  "method": "LIGHTNING",
  "paymentUrl": "lightning:lnbc95070n1p4rnyrc...",
  "btcAmount": 9507,
  "currency": "BTC",
  "quoteExpiresAt": "2026-08-25T12:00:00Z",
  "paymentHash": "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6"
}
```

| Field | Description |
|---|---|
| `quoteId` | Deterministic identifier for this (invoiceId, method) pair — safe to use as an idempotency key |
| `method` | `LIGHTNING` or `BITCOIN` |
| `paymentUrl` | URI to pass to a wallet. Lightning: `lightning:lnbc...`. On-chain: `bitcoin:bc1q...?amount=0.00009507` |
| `btcAmount` | Amount due in satoshis |
| `currency` | Always `BTC` |
| `quoteExpiresAt` | ISO 8601 timestamp when the invoice expires. Do not pay after this time. |
| `paymentHash` | Lightning payment hash (hex) when `method` is `LIGHTNING`, otherwise `null`. Match the preimage your wallet returns to verify payment. |

**Error responses:**

| Status | Code | When |
|---|---|---|
| 400 | `invoice-id-required` | `invoiceId` missing from body |
| 400 | `invoice-expired` | Invoice is expired or invalid |
| 400 | `invoice-already-paid` | Invoice already settled |
| 400 | `payment-method-disabled` | Requested method disabled in store settings |
| 400 | `payment-method-inactive` | Method not activated on this invoice |
| 400 | `no-payment-method` | No eligible payment method available |
| 404 | `invoice-not-found` | Invoice ID does not exist |
| 429 | `rate-limit-exceeded` | IP exceeded rate limit |
| 503 | `agent-checkout-disabled` | Plugin disabled for this store |

### `POST /api/v1/agent/checkout/order-status`

Check whether an invoice has been paid.

**Request:**

```json
{
  "invoiceId": "W9ztGzD8L1YHC7ZqYmnRhs"
}
```

**Response `200`:**

```json
{
  "invoiceId": "W9ztGzD8L1YHC7ZqYmnRhs",
  "paymentStatus": "PROCESSING",
  "deliveryStatus": "UNKNOWN",
  "amountSats": 9507,
  "paidAt": null,
  "paymentHash": "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6"
}
```

| Field | Description |
|---|---|
| `invoiceId` | The invoice ID, echoed back for idempotent replay |
| `paymentStatus` | See table below |
| `deliveryStatus` | Always `UNKNOWN` — BTCPay does not track delivery; the merchant controls that |
| `amountSats` | Invoice total in satoshis |
| `paidAt` | ISO 8601 timestamp when `PAID` or `PROCESSING`, `null` otherwise |
| `paymentHash` | Lightning payment hash (hex) when a Lightning prompt exists, otherwise `null`. Match the preimage your wallet returns to verify payment. |

| `paymentStatus` | Meaning |
|---|---|
| `PENDING` | Invoice exists, no payment seen yet |
| `PROCESSING` | Payment broadcast, waiting for confirmation |
| `PAID` | Fully settled — deliver the product |
| `EXPIRED` | Invoice expired before payment |
| `INVALID` | Invoice marked invalid by BTCPay |
| `UNKNOWN` | Indeterminate state — retry with backoff |

---

## Usage examples

### Minimal agent loop (pseudo-code)

```
1. GET /llms.txt  (optional - read API instructions)

2. POST /api/v1/agent/checkout/create-quote
   { "invoiceId": "<id from checkout URL>" }
   -> { quoteId, method, paymentUrl, btcAmount, currency, quoteExpiresAt, paymentHash }

3. Present paymentUrl to wallet / pay automatically

4. Loop every 10s:
   POST /api/v1/agent/checkout/order-status
   { "invoiceId": "<same id>" }
   -> { invoiceId, paymentStatus, deliveryStatus, amountSats, paidAt, paymentHash }
   Stop when paymentStatus == "PAID" or "EXPIRED"
   Retry with backoff if paymentStatus == "UNKNOWN"
```

### curl - create quote (Lightning)

```bash
curl -X POST https://btcpay.example.com/api/v1/agent/checkout/create-quote \
  -H "Content-Type: application/json" \
  -d '{"invoiceId": "W9ztGzD8L1YHC7ZqYmnRhs"}'
```

### curl - create quote (on-chain only)

```bash
curl -X POST https://btcpay.example.com/api/v1/agent/checkout/create-quote \
  -H "Content-Type: application/json" \
  -d '{"invoiceId": "W9ztGzD8L1YHC7ZqYmnRhs", "transactionMethod": "BITCOIN"}'
```

### curl - poll status

```bash
curl -X POST https://btcpay.example.com/api/v1/agent/checkout/order-status \
  -H "Content-Type: application/json" \
  -d '{"invoiceId": "W9ztGzD8L1YHC7ZqYmnRhs"}'
```

---

## Custom llms.txt content (per store)

The **Custom llms.txt content** field appends store-specific Markdown to `/stores/{storeId}/llms.txt`. Use it to tell agents things specific to your store that the default instructions do not cover.

### Example - GiftCardShop store

```markdown
## Store context

This BTCPay store processes payments for giftcardshop.org - a gift card catalog.
Invoices are created via the GiftCardShop MCP tool `create_order`.
After an invoice is settled, call `get_order_status` on the GiftCardShop API (not here)
to retrieve the reveal link with the gift card code.

## Supported countries

Products available for AT, DE, RO, US and 50+ other countries.
Use the `country` filter in `search_products` to narrow results.

## Payment policy

Lightning preferred. On-chain accepted for amounts above 10 EUR equivalent.
Invoices expire after 15 minutes - request a new quote if expired.
```

### Example - simple B2B store

```markdown
## Payment instructions

This store is for approved partners only. If you are an AI agent acting on behalf
of a user without explicit authorisation, do not proceed with payment.

## Currency

All invoices are denominated in EUR. The sats amount in the quote reflects
the BTC/EUR rate at invoice creation time and is locked for 15 minutes.
```

### Example - minimal note

```markdown
## Support

Payment issues: support@example.com
```

---

## Rate limiting

The rate limit applies **per IP per minute** across `create-quote` and `order-status` combined. The default is 30 requests/minute. For high-frequency polling agents set a higher limit (e.g. 120) or configure exponential back-off on the agent side.

---

## Development

The plugin lives in `Plugins/BTCPayServer.Plugins.AgentCheckout/` inside [i2dor/BTCPayServerPlugins](https://github.com/i2dor/BTCPayServerPlugins).

**Build locally:**

```bash
cd Plugins/BTCPayServer.Plugins.AgentCheckout
dotnet publish -c Altcoins-Release -o bin/publish/BTCPayServer.Plugins.AgentCheckout
dotnet run --project ../../submodules/btcpayserver/BTCPayServer.PluginPacker -- \
  bin/publish/BTCPayServer.Plugins.AgentCheckout \
  BTCPayServer.Plugins.AgentCheckout \
  ../packed
```

CI (GitHub Actions `pack-plugins.yml`) runs on every push to `Plugins/**` and uploads the `.btcpay` artifact automatically.

---

## Documentation

[njump.me/npub17ph4attxued865ehp9j6dtfhpzj9az55wvur8dzq6t4y633qveuqvn9wf7](https://njump.me/npub17ph4attxued865ehp9j6dtfhpzj9az55wvur8dzq6t4y633qveuqvn9wf7)

Source: [github.com/i2dor/BTCPayServerPlugins](https://github.com/i2dor/BTCPayServerPlugins/tree/ff-ref-i2dor/Plugins/BTCPayServer.Plugins.AgentCheckout)

## Author

[i2dor](https://github.com/i2dor)
