# Bellerophon — Generator Requirements (v5)

*Last updated October 12, 2026 (persistence status corrected). Supersedes v4. Companion to "Tangent Atlas — Architecture & Roadmap," which lives in the `TangentAtlas.Platform` repository.*

## How to use this document

This is the specification for Bellerophon, a Roslyn incremental source generator driven by a purpose-built JSON model format. Bellerophon is a tool for building other tools: it generates multi-tenant systems such as the AtlasLedger ledger and, later, stand-alone systems such as the Tangent Atlas control plane.

Most of v4 is now built. **The solution's README is the detailed reference for what is built** (the model format, generated code, transactions, persistence, the change feed, the compliance engine, authorization, the model contract, and naming). This document keeps every requirement with its status, specifies what is still planned, and records what changed from v4 and why (Part I).

Status labels: **Built** (implemented and tested), **Changed** (built differently from v4; the reason is in Part I), **Planned** (specified, not built), **Dropped** (no longer wanted).

**Where it stands.** Generator version 0.4.0, on .NET 10. Projects: `Bellerophon.Modeling`, `Bellerophon.Generator`, `Bellerophon.Runtime`, `Bellerophon.SqlServer`, `Bellerophon.Rules`, `Bellerophon.Rules.NRules`, `Bellerophon.Tool`; samples `AtlasLedger.Server` (Primary), `AtlasLedger.Schema` (Schema), `AtlasLedger.Client` (Replica), `TangentAtlas` (the enums); tests `Bellerophon.Tests`, `AtlasLedger.Tests` (end to end), and `AtlasLedger.Persistence.Tests` (SQL Server). The server persists to SQL Server through the generated writer and recovers every tenant from it at startup. The solution builds with no warnings with naming rules and xUnit's analyzers enforced, and the NRules adapter is built and tested against NRules 1.0.4.

---

## Part A — Generator architecture

**A1. Three layers — Built.** A front end parses model files with exact locations; a semantic model resolves names, applies defaults (implicit `TenantId` and `RowVersion`, the tenant in every reference, the built-in User table), validates, and produces an immutable, value-equatable description; emitters write code from the semantic model only, so the file format can evolve without touching them.

**A2. Projects.**

| Project | Target | Status | Purpose |
|---|---|---|---|
| `Bellerophon.Modeling` | `netstandard2.0` | Built | Parser, semantic model, validation, model writer, fingerprint, navigation naming. No dependencies. |
| `Bellerophon.Generator` | `netstandard2.0` | Built | The incremental generator and its emitters; compiles the modeling sources into itself. Ships `build/Bellerophon.Generator.props`. |
| `Bellerophon.Runtime` | `net10.0` | Built | Fixed server code referenced by generated code (Part G). |
| `Bellerophon.Rules` | `net10.0` | Built | The rules engine host: one session per tenant, in cycles. No dependencies. Referencing it makes the generator emit the rules adapters. |
| `Bellerophon.Rules.NRules` | `net10.0` | Built | Connects NRules sessions to the host. |
| `Bellerophon.SqlServer` | `net10.0` | Built | The SQL Server runtime (Microsoft.Data.SqlClient): schema check, table types, table-valued parameters. Referencing it makes the generator emit the model's SQL writer and loader (D7, D8). |
| `Bellerophon.Tool` | `net10.0` | Built (validation) | The `bell` command: `bell validate`; later baseline promotion, migration scripts, model-diff reports, metrics. |
| `Bellerophon.Analyzers` | `netstandard2.0` | Planned | Analyzers for hand-written code (Part F). |

Only the compiler-hosted projects target `netstandard2.0`; everything else follows `BellerophonTargetFramework` in `Directory.Build.props`. No framework earlier than .NET 10 is supported. Enums and custom types are hand-written C# in a small shared library referenced by both roles.

**A3. Roles — Changed.** **Primary** (server) and **Replica** (client), formerly Master and Slave, selected by the `BellerophonRole` build property. A third role, **Schema**, generates only the Entity Framework model of the database for its migrations (D13). **The server project owns the model file;** a client includes the same file with one line, so the two can never generate from different copies:

```xml
<AdditionalFiles Include="..\AtlasLedger.Server\bellerophon.json" />
```

The package's `build/Bellerophon.Generator.props` makes `BellerophonRole` visible to the compiler and includes a `bellerophon.json` beside the project. The model file is named `bellerophon.json` (not `*.bellerophon.json`); one model per project.

**A4. Incremental pipeline — Built.** Every value between steps is value-equatable; diagnostics travel as data and are created in the output step; type resolution is the only step that reads the compilation, and it extracts only equatable facts (full name, kind, enum members); outputs are split per table and concern (`Ledger.Primary.Account.g.cs`). The generator also detects whether `Bellerophon.Rules` is referenced and emits the rules adapters only then, and likewise for `Bellerophon.SqlServer` and the database code.

**A5. Emission — Built.** Deterministic output, an `<auto-generated>` header and `#nullable enable` on every file, `[GeneratedCode]` on every type, an indented writer, no template engine.

**A6. Dependencies — Built.** The compiler-hosted projects carry no third-party dependencies; an in-house JSON parser records the line and column of every value and accepts comments and trailing commas.

**A7. Diagnostics — Built (BEL0xxx–BEL2xxx).** Every diagnostic points to an exact location in the model file. `BEL0xxx` build configuration (`BEL0001` unrecognized role, `BEL0002` several model files, `BEL0003` C# older than 13, `BEL0004` the Schema role without Entity Framework Core); `BEL1xxx` format (syntax, unexpected type, unknown property, invalid value, duplicate name, missing property, invalid identifier); `BEL2xxx` semantics (unknown datatype, column, or table; reference arity and type; reserved names; invalid policy; unresolved CLR type; durable referencing ephemeral; synced referencing unsynced; size rules; missing access; name conflicts; generated-member collisions; `BEL2016` a durable column type with no SQL Server type, reported only where database code is generated). `BEL3xxx` (schema evolution) and `BEL4xxx` (analyzers) are Planned.

**A8. Testing — Built, with one gap.** Tests drive the generator in memory with `CSharpGeneratorDriver`, compile the output, load it, and exercise it: partitions, navigation, transactions and rollback under concurrency, persistence hooks, the SQL writer and loader against LocalDB, the change feed, the rules engine (with a stand-in and with real NRules), and the server and client end to end over HTTP. Mutation checks confirmed the tests catch planted bugs (wrong rollback order, early lock release, early durability, coalescing, fact equality). *Planned:* snapshot tests of the generated text, so every generator change shows as a reviewed diff. Tests use xUnit v3 on Microsoft Testing Platform v2 (`global.json` opts `dotnet test` in).

**A9. Release archival — Built.** `EmitCompilerGeneratedFiles` writes generated code to `obj/.../generated/`; each release archives it.

---

## Part B — The model format

The README's "The model format" section is the reference. Summary with status:

**B1–B2. File conventions and ordering — Built.** Comments and trailing commas allowed. Object properties are written alphabetically; arrays keep their order; the parser accepts any order and the model is canonicalized, so order never changes the output. *Planned:* a published JSON Schema referenced through `$schema`.

**B3. Top level.**

| Property | Status | Meaning |
|---|---|---|
| `access` | Built | Default read and write scopes; tables may override. |
| `datatypes` | Built | The type dictionary (B4). |
| `name` | Built | Names the root class (`Ledger`) and prefixes file names. |
| `namespace` | Built | Root namespace of the generated code. |
| `tables` | Built | The tables (B5). |
| `version` | Built | Contract version `major.minor`; the major is in the routes. |
| `tenancy` | Planned | `"Standalone"` for single-partition models such as the control plane. Today every model is multi-tenant. |

**B4. Datatypes — Built.** Nothing is built in; an entry is a CLR type or a domain (type plus `maxLength`, `precision`, `scale`). Entries carry no nullability. Enum types and members are resolved from the compilation.

**B5. Tables.**

| Property | Status | Meaning |
|---|---|---|
| `access` | Built | Scopes for this table. |
| `columns` | Built | Column declarations. |
| `concurrency` | Built | `Locked` or `LatestValue` (lock-free immutable records; requires `Ephemeral`). |
| `description` | Built | Documentation. |
| `facts` | Built | `false` keeps the table out of the rules engine's working memory. |
| `key` | Built | The primary key; key columns are immutable; supplied by the client. |
| `persistence` | Built | `Durable` or `Ephemeral`. |
| `plural` | Built | Irregular plural for navigation names. |
| `references` | Built | Parent references (B7). |
| `sync` | Built | `false` keeps the table off the client. |
| `unique` | Built | Unique constraints (B8). |
| `writeMode` | Built | `Mutable` or `AppendOnly`. |
| `api` | Planned | `Full`, `ReadOnly`, `None`. |
| `auditReads` | Planned | Generated reads emit audit events. |
| `deprecated`, `renamedFrom` | Planned | Schema evolution (Part E). |
| `principal` | Dropped | Replaced by the built-in User table (B9). |

**B6. Columns.** C#-style `?` nullability — Built. Object form `type`, `maxLength`, `precision`, `scale`, `description` — Built. `default`, `immutable`, `deprecated`, `renamedFrom` — Planned. `keyGeneration` — **Dropped**: the client supplies every key; the property now draws an unknown-property warning (`BEL1003`). Implicit `TenantId` and `RowVersion` — Built; both names reserved.

**B7. References — Built.** `columns`, `onDelete` (`Block` default, or `Cascade`), `table` (defaults to the reference name). The tenant is implicit. Nullable columns make the reference optional.

**B8. Unique constraints — Built.** Within the tenant; `"idempotent": true` makes a duplicate insert return the stored row.

**B9. Users — Changed.** Every model gets a built-in `User` table: `UserId`, `IdentityIssuer`, `IdentitySubject` (unique together within the tenant), with fixed scopes `Users.Read` and `Users.Write`. The identity columns are ordinary: required, returned, and synced, so an exported user re-imports with its sign-in. Domain columns go in a table that references User (the sample's `Member`). With Entra, the subject is the `oid` claim.

**B10. The ledger model — status.** `samples/AtlasLedger.Server/bellerophon.json` has ten tables: Account, Alert, Dossier (unsynced, own scopes), Edge, Member (no facts), Node, Person, Price (latest value, ephemeral), Symbol, and the built-in User. Still to design: Client and its agreements and documents; the typed relationships between persons and clients; Person's KYC columns; broker connections and feed cursors; orders and executions; securities (today the market feed prices the custodian codes in Symbol); the remaining `NodeStatus` members.

---

## Part C — Semantic validation — Built

Names and types resolve; reserved names are refused; decimals declare precision and scale; strings in keys and unique constraints declare `maxLength`; key columns are non-nullable; reference columns match the parent key in number and type; a durable table never references an ephemeral one; a synced table never references an unsynced one; latest-value tables are ephemeral and neither reference nor are referenced; append-only tables have no update or delete; every table has access scopes; the model's name does not collide with a table; generated member names do not collide. Unknown properties warn (`BEL1003`), which flags properties that were removed, such as `keyGeneration`. *Planned:* the `tenancy` checks for stand-alone models.

---

## Part D — Generated code

**D1. Rows — Built.** Typed properties; key columns and `TenantId` immutable; `RowVersion` is the sequence of the row's last change. Navigation both ways: the parent property named after the reference; children by the child table's plural, singular for one-to-one extensions, decorated with the reference name when it differs from the parent's (`Node.EdgesByParent`). A child holds its parent; a parent holds its children in a set allocated with the first child. No per-row locks.

**D2. Storage — Built.** `Ledger.GetPartition`, `FindPartition`, `DiscardPartition`, `Partitions`, and **`PartitionCreated`**, raised once per new partition so per-tenant services start without polling. A partition holds one dictionary per table and per unique constraint. Latest-value tables are lock-free stores with `Publish{Table}`, `Find{Table}`, and a `{Table}Published` event.

**D3. Locking — Changed.** One exclusive `System.Threading.Lock` per partition (`SyncRoot`, reentrant). No reader/writer or async lock.

**D4. Transactions — Changed.** `LedgerTransaction` is a `ref struct` holding the tenant's lock; the compiler refuses to hold it across an `await`. Inserts, updates and deletes with versions, delete rules, read-your-writes; one undo log replayed newest first on dispose without commit; the partition faults if a rollback ever fails. Nothing outside the data model happens inside a transaction. `Commit` returns the sequence. Single-change shortcuts on the partition. The staged builder is dropped.

**D5. Constraints — Built.** References, unique values, delete rules, immutable keys and tenant; `ConstraintException` and `DBConcurrencyException`. A stored row cannot be passed as its own changes; a row cannot change tenant; an all-zero guid key is refused.

**D6. Sequence and change feed — Built.** One sequence per partition; a coalesced journal (`ReadChangesSince`, `TakeSnapshot`, `WaitForCommitAsync`, `TrimJournal`, `JournalHorizon`); `LedgerChanges.ToFacts()` (parents first, retractions children first).

**D7. Write-behind persistence — Built.** The hooks: dirty-set coalescing, `TakeCut()` into `LedgerCut` (DTOs per table, changed and deleted), `MarkDurable`, `WaitUntilDurableAsync`, `WaitForChangesAsync`, `Fault`, `PendingChangeCount`, `TablesInDependencyOrder`. The flusher is the host's (the sample server's `LedgerFlusher`, with retry and fail-stop), and in the sample server it writes to SQL Server through the generated writer. The writer: `{Model}SqlDatabase.WriteAsync`, generated when the project references `Bellerophon.SqlServer`: table-valued-parameter bulk writes, an UPDATE then an INSERT per table (no MERGE), the deletes, and the watermark, in one round trip and one database transaction (`XACT_ABORT ON`); an overload writes inside the caller's transaction, so a broker cursor can commit with the cut it covers. Table types are named after a hash of their definition and created at startup if missing. Values that would not fit their columns (length, precision, scale) are refused in memory as constraint violations, so the database never refuses or rounds one. Tested against SQL Server LocalDB (`AtlasLedger.Persistence.Tests`): 10,000 inserted rows written in 95 ms, 5,000 updates in 56 ms, 10,000 rows loaded in 137 ms. **The database schema has primary keys only** (D13), and a cut holds each key at most once, so the order of the writes does not matter (tested: `AnyWriteOrderOfACutGivesTheSameDatabase`). Writes coalesce per row; they are not replayed in commit order, which would gain nothing without constraints and would lose the coalescing. `InMemoryLedgerDatabase` remains as a test stand-in.

**D8. Recovery — Built.** `BeginLoad`, `Load{Table}`, `LoadRow`, `CompleteLoad(watermark)`, keeping stored versions; the generated `LoadAsync` reads a tenant in one round trip, parents first; the sample server's `LedgerRecovery` loads every tenant before anything else starts, and a tenant that fails to load stops the server. Enumerations are stored as numbers; renumbering one is a data migration (export by name, change, reload). *Planned:* in-memory backfills for added columns (Part E).

**D9. API controllers — Built, routes changed.** `v{major}/tenants/{tenantId}/{table}` with list, get by key, insert, update, and delete (`?rowVersion=`); 412, 422, and 400 mapped by `MapModelExceptionsAttribute`; every write answers only once durable. `I{Table}Store` and `LedgerStore`. *Dropped:* the `Idempotency-Key` header (identical re-inserts and version checks make retries safe; open question in the architecture document). Multi-word tables route in kebab case (`order-intent`). *Planned:* `traceId` on every problem response; filtering and sorting (deferred).

**D10. Sync endpoint — Planned.** Pull with long polling: `GET v{major}/tenants/{tenantId}/changes?since={sequence}` returns each changed row once in its latest state and a tombstone per deleted key, up to the durable watermark; waits for the next durable commit when there is nothing new; a reader behind the journal horizon is told to reload from a snapshot. The journal already provides each piece.

**D11. Authorization — Built.** `Ledger` implements `ITenantMembership` from each tenant's User table; `LedgerAccessPolicies` per table; 401, 404 (not a user of the tenant), 403 (missing scope); tools skip membership only when the token is an application's (`idtyp` = `app`, no scope) and its client ID is in `Tools:ClientIds` (placeholders until the registrations exist), and every tool access is logged; `LedgerTenantsController` (`GET v{major}/me/tenants`). With Entra, `idtyp` must be added as an optional access-token claim on the API registration. *Planned:* a change-feed publisher of (identity, tenant) pairs, with no personal data, for the control plane's routing index.

**D12. Read auditing — Planned.**

**D13. Entity Framework model — Built (Schema role), scope reduced.** Entity configuration for the relational schema and its migrations only: `{Model}DbContext` and one entity per durable table, generated in the Schema role with the writer's column types (a test checks every column agrees), plus the watermark table; the sample's `AtlasLedger.Schema` holds the first migration. Writing and loading use the generated SQL of D7 and D8. **Primary keys only:** no unique constraints, no foreign keys, and no secondary indexes. The ledger enforces uniqueness and references in memory, the loader checks them again on recovery, and the database is read only by the loader, a table at a time, so constraints and indexes would cost every flush and protect nothing. The cost to manage is that a generator bug that wrote inconsistent rows would surface at the next load rather than at the flush; recovery drills (E6) catch it early.

**D14. Rules adapters — Built.** One immutable fact per table (`AccountFact`): equal by key, `RowVersion` carried but not compared, `partial` for computed properties; tables with `"facts": false` and the User table excluded. With `Bellerophon.Rules` referenced: `LedgerRulesTenant` (snapshot, changes since a sequence, conflated latest values, commit and price waits, decisions applied in one transaction) and `LedgerRuleActions` (where rules record decisions).

**D15. Replica — Built; sync client Planned.** Synced tables only; `Apply{Table}` and `Remove{Table}` in transactions; `Ledger.AddContractHeaders`. *Planned:* a sync client using D10 that tracks the last applied sequence per tenant and handles reload and 426.

**D16. Stand-alone mode — Planned.** A single partition, no tenant column, for the control plane.

**D17. Batch endpoint — Built.** `POST v{major}/tenants/{tenantId}/batch`: insert, update, and delete operations across tables, authorized first, read next, applied in one transaction; one result per operation. At most `Batch:MaximumOperations` operations (10,000 by default, about 7 ms of lock time as measured); a larger batch is 413 and names the limit.

**D18. Model contract — Built.** `Ledger.Version`, `Ledger.Fingerprint` (SHA-256 of a canonical description of the model's meaning, including built-in tables and enum members, excluding descriptions and `facts`), the contract headers, and `UseLedgerContract()` answering 426 on mismatch. Exact match for now.

**D19. Data transfer objects — Built.** One per table, `{Table}Dto` in the root namespace: every column, keys included, plus `RowVersion`, never the tenant. Used in both directions, in JSON scripts, and in cuts.

**D20. Hosting — Built.** `AddLedger()` and `UseLedgerContract()`.

**D21. Typed API client — Planned.** Generated for the Replica role from the same model: one method per table and action plus the batch, the contract headers on every request, refusals as typed exceptions. The sample client's hand-written `LedgerApiClient` shows its shape.

**D22. Market data stream — Planned (generated or hand-written to be decided).** A server-sent event stream per tenant for latest-value tables, with conflated frames.

---

## Part E — Schema evolution — Planned

Unchanged in substance from v4:
- **E1. Baseline.** At each release the tool copies the model to `bellerophon.baseline.json` with the enum members resolved from the compilation and the minimum client version supported. Every build compares against it.
- **E2. Classification.** Additive and automatic: a new table; a new nullable column or one with a default; a new enum member; a longer `maxLength`; non-nullable to nullable. Declared: renames (`renamedFrom`); removals only after a release marked `deprecated`. Build errors (`BEL3xxx`): type changes, shorter `maxLength`, nullable to non-nullable, key, persistence, or write-mode changes, new references or unique constraints on existing columns, removed enum members.
- **E3. Migrations.** Database migrations from the baseline-to-model diff (through the Entity Framework model of D13); backfills in memory during recovery, persisted by the flusher.
- **E4. Client compatibility.** The fingerprint's exact match relaxes to: same major version, and the client's minor version no higher than the server's.
- **E2a. Enumeration numbers — Deferred.** Enumerations are stored as numbers, so a renumbered member would be misread at the next load. For now renumbering is a deliberate data migration and nothing checks it. When it is worth the cost: explicit numbers in the stored enumerations and a test that lists them, then a build error from the baseline when a number changes.
- **E5. Release artifacts.** A model-diff report per release and the ratio of generated to hand-written code.
- **E6. Recovery drills — Built as tests.** `RecoveryDrillsLoadEveryTenantAsItWas` (SQL Server and in memory). Still to do: running them against production data. With no constraints in the database (D13), the loader is the only check on what was written. Load each tenant from the database into a scratch partition regularly (and in crash-injection tests) and compare it with the live partition, so a bad write is found while the live tenant still holds the truth.

---

## Part F — Analyzers for hand-written code — Planned

The compiler already enforces the most important rule (no transaction across an `await`). Remaining candidates:
- **BEL4001:** anything marked as an external side effect (sending an order to a broker) must follow awaiting durability of the sequence it depends on.
- **BEL4002:** controllers and services must not use the Entity Framework context for ledger data.
- **BEL4003:** no second transaction may be opened on a partition while one is open in the same method (today a runtime check).
- **BEL4004:** rows and partition collections must not escape the lock or transaction they were read under.

---

## Part G — Runtime library

`Bellerophon.Runtime` (built): OAuth scope and identity claim helpers (`ScopeClaims`, `IdentityClaims`); the tenant membership authorization handler; the model contract middleware; `MapModelExceptionsAttribute`; the batch request types and reader; service registration helpers.

`Bellerophon.SqlServer` (built): the schema check, table-type creation, and table-valued-parameter support used by the generated writer and loader.

`Bellerophon.Rules` (built): `RulesEngineHost`, `TenantRulesEngine`, `RulesEngineOptions` (minimum interval, retry delay, time provider), the session and tenant contracts, cycle results and events, and a concurrency gate. `Bellerophon.Rules.NRules` (built): `NRulesSessionFactory` (each tenant's session resolves rule dependencies from the tenant first) and `NRulesSession` (update by key, else insert; retract by key).

Changed or dropped from v4: **G1 `AsyncTransaction`** — Dropped (the transaction is synchronous). **G2 `LedgerControllerBase`** — Replaced by the generated controllers and `MapModelExceptionsAttribute`; `traceId` on problem responses still Planned. **G3 async reader/writer lock** — Dropped. **G4 sequencing and durable waits** — Built, generated into each partition. **G5 flusher host** — hand-written in the sample server (`LedgerFlusher`, writing through the generated SQL writer); now that the writer and loader are generated, the flusher and `LedgerRecovery` are candidates to move into a reusable runtime host.

---

## Part H — Hand-written components and their contracts

- **H1. Rules:** NRules rules compiled once; each receives its tenant's `LedgerRuleActions` as a dependency and records decisions there, never writing the ledger itself. Rule actions are expression trees, which cannot name the transaction, so they call ordinary methods that record the change. Derived and denormalized facts are designed in the rules. Every order must trace to the customer's own instruction.
- **H2. Market data ingestion:** one feed per broker connection, publishing into each tenant's latest-value store (simulated in the sample server).
- **H3. Order router and broker adapters:** orders are durable rows; the router awaits durability before sending, with the client order id; broker events are inserted idempotently; each connection's cursor is persisted in the same flush as the events it covers.
- **H4. Reference-data publisher:** versioned releases adopted by each tenant in one transaction.
- **H5. Control-plane integrations:** payment-processor webhooks applied idempotently; the routing index fed by D11.
- **H6. The flusher and startup recovery** (`LedgerFlusher`, `LedgerRecovery`): hand-written hosts around the generated writer and loader (D7, D8).
- **H7. Other transactions:** through the generated transaction only, in synchronous methods.

---

## Part I — Changes from v4

| v4 | v5 | Why |
|---|---|---|
| Master and Slave | Primary and Replica | Clearer, conventional names. |
| `*.bellerophon.json`, possibly several per model | `bellerophon.json`, one per project; the server owns it; the client includes it with `AdditionalFiles` | Client and server can never generate from different copies. |
| No contract version | `version` (`major.minor`, major in routes) and a fingerprint; 426 on mismatch | A client built from another model must be refused, not misread. |
| `keyGeneration`, server-generated single-guid keys | **Dropped:** the client supplies every key | Data moves through JSON scripts with nothing generated between the file and the ledger; one batch can create a parent and its children. |
| Separate request and response shapes implied | One DTO per table, both directions | With client keys there is no difference to model. |
| `principal` on a user table; identity columns never returned | Built-in User table; identity columns ordinary and returned | An exported user must re-import with its sign-in; no write-only special case. |
| Parent references only | Navigation both ways, with naming rules and `plural` | The old ledger's navigation was valued; names must never change when relationships are added. |
| One reader/writer lock per partition (DotNext, async) | One exclusive `System.Threading.Lock` | Write-heavy workload, microsecond holds; async locks exist for holding across I/O, which is forbidden. |
| Staged builder, `AsyncTransaction` | `ref struct` transaction with an undo log | The compiler enforces "never across an await"; rollback is exact. |
| Entity Framework writes and loads | Entity Framework for the schema and migrations; generated SQL for writes and loads | Change tracking was the bulk-write bottleneck in the old system. |
| `Idempotency-Key` header on writes | Not built | Identical re-inserts return the stored row; retried updates and deletes get 412. Open question. |
| Routes `.../tenants/{tenantId}/ledger/[controller]` | `v{major}/tenants/{tenantId}/{table}` | Shorter; the tenant stays in the route for authorization and routing. |
| Rules-engine fact adapters (sketch) | Facts, change feed, rules tenant, rule actions; the `Bellerophon.Rules` host; NRules adapter | Built to the old NRules pipeline's outline, compatible with the NRules package in use. |
| — | `facts`, `sync`, `plural`, `access`, `name`, `version`; batch endpoint; `PartitionCreated` | Needs found while building. |

| Roadmap phase (architecture document) | Requirements | Status |
|---|---|---|
| 1 — The new generator | A1–A9, B1–B8, C, D1, D19 | Built (snapshot tests and JSON Schema to do) |
| 2 — Model guarantees | B9, D5, D11, D18, E1–E5, F, D16 | Built except schema evolution, analyzers, stand-alone mode |
| 3 — Partitions and the lock model | D2–D4, D6, D9, D17, D10 | Built except D10 |
| 4 — Write-behind persistence | D7, D8, D13 | Built; the server writes to SQL Server and recovers from it; crash injection in a running server to do |
| 5 — Real-time tier | D2 latest values, D14, D22; H1–H3 | Engine built; feeds, orders, router, D22 to do |
| Client | D15, D21 | Mirror built; sync client and typed API client to do |
