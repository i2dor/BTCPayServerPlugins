NIP-XX
======

Merchant Offers and Payment Receipts
------------------------------------

`draft` `optional`

This NIP defines two signed statements a merchant issues for an invoice: an offer, before payment, committing to the terms; and a receipt, after, stating what was paid and what was delivered. Together they give a payer (typically an autonomous agent) portable, independently verifiable evidence, instead of trusting an HTTP status code or a wallet balance change.

## Motivation

Agents that pay invoices on their own need an evidence trail that survives the session: something they can show to their operator, to an auditor or to another agent. A Lightning preimage proves that a payment reached its destination, but it does not say what the payment was for, which merchant issued the invoice, or whether anything was delivered. This NIP binds those facts together under the merchant's key.

Payment and delivery are separate verdicts. A receipt can state that an invoice is paid while delivery is still unknown.

## Offer

An offer is a regular event of kind `1402`, signed by the merchant's receipt key and returned to the payer before payment. Being a regular event, it cannot be replaced: it is a fixed commitment to the terms below.

```jsonc
{
  "kind": 1402,
  "pubkey": "<merchant receipt key, hex>",
  "created_at": <unix time when the offer was issued>,
  "content": "",
  "tags": [
    ["invoice", "<invoice id>"],
    ["amount", "118000"],
    ["price", "0.50", "RON"],
    ["method", "LIGHTNING"],
    ["bolt11", "lnbc1180n1..."],
    ["payment_hash", "2f07e62403129d2b1c8ceae5af1f62f13ed636a3af60437cb5dddd464d817d55"],
    ["quote", "a3f9c2b1d4e87f20"],
    ["valid_until", "1762481234"],
    ["store", "<merchant defined store identifier>"]
  ],
  "id": "...",
  "sig": "..."
}
```

| tag | value | required | meaning |
|---|---|---|---|
| `invoice` | invoice id | yes | Invoice the offer is for. |
| `amount` | millisatoshis, string | yes | Amount the payer must send. |
| `price` | amount, currency code | no | Price in the invoice currency, same shape as NIP-99. |
| `method` | `LIGHTNING` or `BITCOIN` | yes | One offer per method. |
| `bolt11` | BOLT11 invoice | when method is `LIGHTNING` | The exact invoice to pay. |
| `payment_hash` | hex | when method is `LIGHTNING` | Must equal the payment hash in `bolt11`. |
| `address` | Bitcoin address | when method is `BITCOIN` | The exact address to pay. |
| `quote` | quote id | no | Merchant side identifier of this quote. |
| `valid_until` | unix time, string | yes | The payer MUST NOT pay after this time. |
| `store` | string | no | See the receipt tags. |

Before paying, the payer SHOULD check the signature, that `pubkey` is the merchant key it expects, that `bolt11` decodes to `amount` and `payment_hash` (or that `address` is what it is about to pay), and that `valid_until` is in the future. If a payer pays within an offer and the merchant later disputes the terms, the offer is the merchant's own signed statement of them.

## Receipt

A receipt is an addressable event of kind `31402`, signed by the merchant's receipt key.

```jsonc
{
  "kind": 31402,
  "pubkey": "<merchant receipt key, hex>",
  "created_at": <unix time when the receipt was issued>,
  "content": "",
  "tags": [
    ["d", "<invoice id>"],
    ["e", "<id of the kind 1402 offer the payer acted on>"],
    ["status", "PAID"],
    ["delivery", "UNKNOWN"],
    ["amount", "118000"],
    ["price", "0.50", "RON"],
    ["method", "LIGHTNING"],
    ["payment_hash", "2f07e62403129d2b1c8ceae5af1f62f13ed636a3af60437cb5dddd464d817d55"],
    ["quote", "a3f9c2b1d4e87f20"],
    ["quote_expires_at", "1762481234"],
    ["paid_at", "1762480100"],
    ["store", "<merchant defined store identifier>"]
  ],
  "id": "...",
  "sig": "..."
}
```

`content` SHOULD be empty. Implementations MAY put a short human readable summary there. Clients MUST NOT rely on it.

### Tags

| tag | value | required | meaning |
|---|---|---|---|
| `d` | invoice id | yes | Unique per merchant key. Identifies the invoice this receipt is about. |
| `e` | offer event id | no | The kind `1402` offer the payment corresponds to, when the merchant issued one. Repeat if payments were made under several offers. |
| `status` | `PENDING`, `PROCESSING`, `PAID`, `EXPIRED`, `INVALID`, `UNKNOWN` | yes | Payment verdict at `created_at`. |
| `delivery` | `UNKNOWN`, `DELIVERED`, `FAILED` | yes | Delivery verdict at `created_at`. Independent of `status`. |
| `amount` | millisatoshis, string | yes | Total owed at the locked exchange rate. |
| `paid` | millisatoshis, string | no | Total received so far, when it differs from `amount` (partial or over payment). |
| `price` | amount, currency code | no | Price in the currency the invoice was created in, same shape as NIP-99. |
| `method` | `LIGHTNING` or `BITCOIN` | when paid | Payment method used. Repeat the tag if both were used. |
| `payment_hash` | hex | when method is `LIGHTNING` | Payment hash of the paid BOLT11 invoice. |
| `txid` | hex | when method is `BITCOIN` | Transaction that paid the invoice. Repeat for multiple transactions. |
| `quote` | quote id | no | Identifier of the quote the payer acted on. |
| `quote_expires_at` | unix time, string | no | Expiry of that quote or invoice. Not to be confused with NIP-40 `expiration`, which this NIP does not use. |
| `paid_at` | unix time, string | when paid | Time the last payment was received. |
| `result` | sha256 hex | when `delivery` is `DELIVERED` | Hash of the delivered result (for example a voucher code, a file or an API response), so the payer can prove what was delivered without the receipt revealing it. |
| `store` | string | no | Merchant defined identifier, for merchants that issue receipts for several stores with one key. |

Unknown tags MUST be ignored.

### Preimages

A receipt MUST NOT contain the Lightning preimage. Receipts may be served by unauthenticated endpoints or published to relays, and anyone holding the preimage could present it as their own proof of payment. The payer already holds the preimage from its wallet. Proof of payment is the pair: a receipt with `payment_hash` signed by the merchant, plus a preimage whose sha256 equals that hash.

## Replacement

Because the kind is addressable, a newer receipt for the same `d` replaces an older one on relays (for example when `status` moves to `PAID` or `delivery` moves to `DELIVERED`). Payers SHOULD keep every receipt they receive, since an older receipt is still a valid signed statement of the state at its `created_at`.

Merchants MUST NOT issue a receipt that moves a final verdict backwards (for example `PAID` to `PENDING`). If a payment is reversed or found invalid, the merchant issues `INVALID` explicitly.

## Key discovery

A merchant publishes its receipt public key over the same channel the payer uses to find the merchant, for example:

* a line `Receipt signing key: npub1...` in the merchant's `llms.txt`,
* a NIP-05 identifier on the merchant's domain,
* the merchant's checkout or API documentation.

Payers SHOULD record the key at quote time and reject receipts for that invoice signed by any other key.

### Trust model

A receipt proves that the holder of the merchant key made a statement. It does not prove the statement is true, and it does not prove that the key belongs to who the payer thinks it does. Its value is accountability: a merchant that signs `PAID` and `DELIVERED` and then denies it is contradicted by its own signature.

## Verification by a payer

1. Check the event signature and that `kind` is `31402`.
2. Check that `pubkey` matches the key recorded at quote time, which is the `pubkey` of the offer.
3. Check that `d` is the invoice the payer paid and, if present, that `e` points to the offer the payer acted on.
4. For Lightning, check that sha256(preimage from the wallet) equals `payment_hash`.
5. Treat `status` and `delivery` as separate verdicts. Treat `UNKNOWN` as transient and ask again with backoff.

A complete evidence trail for one purchase is: the offer, the preimage (or the payer's own transaction), and the latest receipt.

## Publishing

Receipts are delivered to the payer directly, for example in an HTTP response. Publishing receipts to relays is OPTIONAL and SHOULD be disabled by default: public receipts reveal a merchant's sales volume, amounts and timing. A merchant that publishes SHOULD use relays it controls or trusts.

Offers SHOULD NOT be published to relays at all. They exist to be held by the payer.

## Relation to other work

* **NIP-57.** Zap receipts (kind `9735`) cover zaps, where the payer starts from a zap request and the recipient is a Nostr user. Merchant receipts cover commercial invoices created by the merchant, where no zap request exists, and add a delivery verdict. `amount` uses millisatoshis as in NIP-57.
* **NIP-15.** The marketplace order status message carries `paid` and `shipped` flags, but as an encrypted direct message inside the NIP-15 checkout flow, without a payment hash or transaction id. Receipts here are standalone signed events that a third party can verify without being part of the conversation. A NIP-15 merchant could issue them alongside its status messages.
* **NIP-99.** `price` follows the shape used there.
* **x402 Offer and Receipt extension.** x402 defines a similar pair (a signed offer on HTTP 402, a signed receipt on success) using JWS or EIP-712, with keys discovered through `did:web` or DNS. Its receipt is deliberately minimal (no amount, optional transaction, no delivery evidence) and the extension targets EVM style networks. This NIP covers Bitcoin and Lightning, binds the receipt to a payment hash or transaction, and separates payment from delivery. The two are complementary: a merchant serving x402 clients can sign the same facts as a JWS with the same secp256k1 key (ES256K), though the signature scheme differs from Nostr's Schnorr signatures.
