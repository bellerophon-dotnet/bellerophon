# Glossary

Industry, protocol, and project terms used in names and documentation. The naming guideline (see "Naming" in README.md) allows
a standard abbreviation when its reader knows it; this page is for the reader who doesn't yet. Add a term when you introduce it.

## Wealth management and trading

| Term | Meaning |
|---|---|
| **Account** | A brokerage account. In the model, an account is a kind of node (`AccountId` equals its `NodeId`). |
| **Custodian** | The firm that holds an account's assets, such as Schwab. `Custodian` is also a symbol scheme: the custodian's account number. |
| **Cusip** | Committee on Uniform Securities Identification Procedures: the nine-character identifier of a North American security. |
| **Execution** | One fill of an order at the broker: a quantity at a price. An order can have several. |
| **Kyc** | Know Your Customer: the identity, financial, and suitability information a firm must hold about each client. |
| **Nav** | Net asset value: the market value of a position or portfolio, which changes with every tick. |
| **Ria** | Registered investment adviser: a firm that manages clients' money under SEC or state registration. |
| **Tick** | One price update from the market. Ticks are ephemeral; they are never stored durably. |

## Identity and access

| Term | Meaning |
|---|---|
| **Claim** | One statement inside a sign-in token, such as the caller's identity or permissions. |
| **Entra** | Microsoft Entra ID, Microsoft's identity service. Entra External ID signs in customers. |
| **`iss`** | The issuer claim: which identity provider issued the token. Wrapped as `IdentityClaims.IssuerClaim`. |
| **`oid`** | Microsoft's object identifier claim: one person's identity, the same in every application. Wrapped as `IdentityClaims.ObjectIdentifierClaim`. |
| **`roles`** | The claim carrying an application's own permissions, when a tool acts as itself rather than for a person. |
| **Scope** | A permission an application holds on a person's behalf, such as `Ledger.Read`. Carried in the `scp` claim (Microsoft) or `scope` claim (the OAuth standard). |
| **`sub`** | The standard subject claim. Microsoft's differs per application, so it isn't used when `oid` is present. |

## The data model and Bellerophon

| Term | Meaning |
|---|---|
| **Bellerophon** | The code generator. Model files are `bellerophon.json`, the tool is `bell`, and diagnostics are `BELnnnn`. |
| **Change feed** | A partition's committed changes after a sequence, in commit order and coalesced, read by the rules engine and, later, client sync (`ReadChangesSince`). |
| **Conflation** | Keeping only the latest value for each key, so a burst of updates becomes one (prices, before each rules cycle). |
| **Cut** | A copy of every row a tenant changed since the last flush, taken between transactions, for the flusher to write as one unit (`LedgerCut`). |
| **Dto** | Data transfer object: the flat form of a row that travels over the API, appears in JSON scripts, and is written to the database (`AccountDto`). |
| **Fact** | A row in the rules engine's working memory: an immutable snapshot, equal to any other fact with the same key (`AccountFact`). |
| **Fail-stop** | When a tenant's changes can't be persisted, it stops (`Fault`) and is reloaded rather than carrying on with changes it can't keep. |
| **Fingerprint** | A hash of the model's meaning, embedded in server and client so each can tell whether the other was built from the same model. |
| **Journal** | A partition's record of committed changes, one live entry per changed row, behind the change feed. Its horizon is the oldest sequence it still covers. |
| **Latest value** | A lock-free table holding only the most recent record for each key, such as the latest price per symbol. |
| **Partition** | One tenant's rows, held in memory under one lock. |
| **Primary** | The authoritative copy of the data, on the server, and the role that generates it. |
| **Rete** | The matching algorithm behind NRules: it remembers partial matches, so each change re-evaluates only the rules it affects. |
| **Replica** | A synchronized copy of the synced tables, on a client, and the role that generates it. |
| **Row version** | The tenant's sequence number at a row's last change, used to refuse stale updates and deletes. |
| **Rule actions** | Where rules record their decisions during a cycle, committed after the rules fire in one transaction (`LedgerRuleActions`). |
| **Rules tenant** | A tenant as the rules engine sees it: facts from its change feed and latest values, decisions committed through its transactions (`LedgerRulesTenant`). |
| **Tenant** | One customer's isolated data: one firm, or one direct customer. Tenants share nothing. |
| **Transaction** | A unit of work on one partition: every change commits together, or none does. |
| **Watermark** | The durable sequence: every transaction up to it is in the database. Writers answer, clients sync, and orders go out only up to it. |
| **Write-behind** | Persisting committed changes after the fact, outside the lock, rather than inside each transaction. |
