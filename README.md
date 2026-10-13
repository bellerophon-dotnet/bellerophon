# Bellerophon

Model-driven code generation for .NET. Bellerophon could not defeat the Chimera alone, so he harnessed Pegasus. One engineer cannot hand-build a real-time order management system either, so this project harnesses code generation.

You declare a data model in one JSON file; a Roslyn incremental source generator turns it into C# during every build, in the IDE and on the command line alike. The same model generates both sides of the system: the server (rows, stores, controllers, access policies) and the client (the rows a client may hold).

Design documents: [docs/generator-requirements.md](docs/generator-requirements.md) (every generator requirement with its status). This README documents what is built. The architecture of Tangent Atlas, the product built on Bellerophon, and its roadmap are kept with the product, in the `TangentAtlas.Platform` repository (`Documents/`), not here.

## Layout

| Path | Target | Purpose |
|---|---|---|
| `src/Bellerophon.Modeling` | netstandard2.0 | JSON parser with exact locations, semantic model, validation, model writer, fingerprint. No dependencies. |
| `src/Bellerophon.Generator` | netstandard2.0 | The incremental source generator, plus `build/Bellerophon.Generator.props` for consuming projects. |
| `src/Bellerophon.Runtime` | net10.0 | Fixed server code used by generated code: scope and identity checks, the tenant membership gate, the contract check. |
| `src/Bellerophon.Rules` | net10.0 | The compliance engine's host: one rules session per tenant, evaluated in cycles. No dependencies; referencing it makes the generator emit the model's rules adapter. |
| `src/Bellerophon.Rules.NRules` | net10.0 | Connects NRules: one NRules session per tenant from your compiled rules. |
| `src/Bellerophon.SqlServer` | net10.0 | The SQL Server runtime (Microsoft.Data.SqlClient): schema check, table types, table-valued parameters. Referencing it makes the generator emit the model's `{Model}SqlDatabase`, the flusher's writer and the recovery loader. |
| `src/Bellerophon.Tool` | net10.0 | The `bell` command: `bell validate <path to bellerophon.json>`. |
| `samples/TangentAtlas` | net10.0 | Stands in for the hand-built TangentAtlas package that supplies the enums the model names. |
| `samples/AtlasLedger.Server` | net10.0 | The seed of the product server (Primary role), and the owner of the model (`bellerophon.json`). Hosts the generated API on a fixed port until stopped, with write-behind persistence, the NRules compliance engine, and a simulated market feed. |
| `samples/AtlasLedger.Schema` | net10.0 | The database schema (Schema role): an Entity Framework Core model generated from the server's model file, and its migrations. Used to create and evolve the database, never to read or write the ledger. |
| `samples/AtlasLedger.Client` | net10.0 | The seed of the product client (Replica role), generated from the server's model file. Works with a running server through its API: tenants, JSON script load and export, alerts, a local copy of a tenant, and an end-to-end walkthrough. No ASP.NET Core. |
| `tests/Bellerophon.Tests` | net10.0 | xUnit v3 tests, including generator tests driven by `CSharpGeneratorDriver`. |
| `tests/AtlasLedger.Tests` | net10.0 | End-to-end tests: the real server and client in one process, over HTTP on a free port, including recovery at startup. |
| `tests/AtlasLedger.Persistence.Tests` | net10.0 | Database tests against a real SQL Server (LocalDB on Windows, or `BELLEROPHON_SQL_SERVER`); each test creates, migrates, and drops its own database. Skipped, with the reason, when no server can be reached. |

Only the compiler-hosted projects are pinned to netstandard2.0, because the compiler that loads them requires it. Everything else, including every project that consumes the generated code, targets .NET 10 through `BellerophonTargetFramework` in `Directory.Build.props`; no earlier framework is supported.

## Developing

- **Inner loop: the tests.** `tests/Bellerophon.Tests` runs the generator in memory with Roslyn's `CSharpGeneratorDriver`, so breakpoints in the generator and emitters work like ordinary code. Note that `Initialize` only builds the pipeline; the work happens afterward, in the lambdas it registers.
- **End-to-end check: the samples.** Build them to see the generated files under `obj/<configuration>/<tfm>/generated/`. Run the server, then the client's walkthrough against it (see [The server and client](#the-server-and-client)); `tests/AtlasLedger.Tests` does the same in one process.
- **F5 on the generator.** `src/Bellerophon.Generator/Properties/launchSettings.json` debugs the generator while it processes the server sample. It needs the ".NET Compiler Platform SDK" component from the Visual Studio installer.
- Visual Studio keeps a loaded generator in memory, so after changing generator code, restart Visual Studio or rely on the tests and command-line builds.

## Dependency notes

- **Tests use xUnit v3 (4.0.1) on Microsoft Testing Platform v2.** The `xunit` package (v2) has been replaced by `xunit.v3`. The test project is an executable (`<OutputType>Exe</OutputType>`) and references only `xunit.v3`: no `Microsoft.NET.Test.Sdk`, no `xunit.runner.visualstudio`, since VSTest isn't used. `global.json` opts `dotnet test` into the platform; without it, the .NET 10 SDK refuses to run the tests. You can also run the test executable directly. Tests take the test's cancellation token from `TestContext.Current.CancellationToken` (analyzer rule xUnit1051). `ITestOutputHelper` is in the `Xunit` namespace, and `IAsyncLifetime` returns `ValueTask`.
- **NRules 1.0.4.** `Bellerophon.Rules.NRules` references the `NRules` package, which brings in `NRules.Fluent`, `NRules.Runtime` and `NRules.RuleModel`. The adapter, the server sample's rules (`samples/AtlasLedger.Server/Compliance/ComplianceRules.cs`) and `NRulesTests` are built and run against 1.0.4. NRules identifies a fact by `Equals`, which is why generated facts are equal by key: a newer fact for a row updates the old one, and a retraction removes it whatever its values.
- **xunit.v3 4.0.1** depends on `xunit.v3.mtp-v2` (Microsoft Testing Platform v2). Whether `xunit.runner.visualstudio` is still needed under v2 has not been verified.

## Build, test, run

```
dotnet build
dotnet test
dotnet run --project samples/AtlasLedger.Server          # runs until Ctrl+C; in memory unless ConnectionStrings:Ledger is set
dotnet run --project samples/AtlasLedger.Client          # in a second terminal: the walkthrough
dotnet run --project src/Bellerophon.Tool -- validate samples/AtlasLedger.Server/bellerophon.json
```

## Using the generator in a project

The samples' project files contain exactly what a consumer writes. The one exception is the `Import` line, which stands in for the NuGet package reference; the package imports `build/Bellerophon.Generator.props` automatically.

There are two roles for application code. **Primary** is the authoritative copy, on the server: rows, partitions, transactions, persistence hooks, stores, controllers, and policies. **Replica** is a synchronized copy of the synced tables, for clients. A third role, **Schema**, generates only the database's Entity Framework model for migrations (see [SQL Server](#sql-server)). The server owns the model: its `bellerophon.json` sits in the project folder and is included automatically.

```xml
<PropertyGroup>
  <BellerophonRole>Primary</BellerophonRole>
</PropertyGroup>
```

A client generates from the server's file, with one standard line, so server and client always share one model:

```xml
<PropertyGroup>
  <BellerophonRole>Replica</BellerophonRole>
</PropertyGroup>
<ItemGroup>
  <AdditionalFiles Include="..\AtlasLedger.Server\bellerophon.json" />
</ItemGroup>
```

A project has exactly one model file; a second is a build error. The generated code requires C# 13 or later (the .NET 10 SDK's default); an older language version is the build error `BEL0003`, and nothing is generated. Model problems appear in the Error List as `BELnnnn` errors pointing at the exact line and column.

## The server and client

The two samples are the seeds of the product's server and client, and keep the product's division of labor: the server owns the data, the rules, and the market feed; the client works only through the API.

**The server** (`samples/AtlasLedger.Server`) listens on `http://127.0.0.1:5180` (`appsettings.json`, overridable on the command line, for example `--urls`) and runs until stopped. `AtlasLedgerServer.Build` assembles it, so tests can host the same server:

- `Persistence/`: `LedgerFlusher` writes each tenant's cuts to an `ILedgerDatabase`, retrying with backoff and stopping the tenant if writes keep failing. `SqlServerLedgerDatabase` wraps the generated SQL Server writer and loader and is used when `ConnectionStrings:Ledger` is set; `InMemoryLedgerDatabase` is used otherwise, for development and tests, and nothing in it survives a restart. `LedgerRecovery` loads every tenant before anything else starts (see [SQL Server](#sql-server)).
- `Compliance/`: the NRules rules (`ComplianceRules.cs`) and `ComplianceService`, which starts an engine per tenant, logs decisions, and trims each tenant's change journal to what its engine still needs.
- `Market/`: `SimulatedMarketFeed` prices every held symbol with a random walk and sends an occasional bad print (a zero price, corrected on the next tick). A real feed replaces this class only.
- `Security/`: `DevelopmentAuthentication` signs callers in from a `Development-Caller` header (`tool` or `advisor`) with the claims the identity provider issues. The server refuses to start outside the Development environment until bearer tokens are wired in.
- Per-tenant services start from `Ledger.PartitionCreated`, raised once for each new partition, rather than polling. A health check answers at `/health`.

**The client** (`samples/AtlasLedger.Client`) is a command-line program (`--help` lists everything):

```
AtlasLedger.Client walkthrough [--market-wait 30]   # exercise the server end to end; exits non-zero if anything is off
AtlasLedger.Client tenants --as advisor             # the tenant picker
AtlasLedger.Client load <tenant> Data/firm-a.json   # load a JSON script as one batch
AtlasLedger.Client export <tenant> [file.json]      # export a tenant as a script that loads anywhere
AtlasLedger.Client alerts <tenant> --watch          # watch the compliance alerts change
AtlasLedger.Client mirror <tenant> --as advisor     # copy a tenant locally (Replica) and show its graph
```

- `Api/LedgerApiClient` calls the API with the generated DTOs and contract headers, and turns refusals into `LedgerApiException` with the status code. It is hand-written until Bellerophon generates the typed client.
- `Scripts/LedgerScript` is the JSON script format, the same as the batch endpoint's request: loading and exporting do nothing to the rows between the file and the ledger.
- `Mirror/LedgerMirror` refreshes a local Replica of a tenant, table by table, parents first, until the sync endpoint exists.
- The walkthrough checks the status of every step it takes, so it doubles as a smoke test of a deployed server. Each run uses new tenants, so it can run against the same server repeatedly.

## The model format

```json
{
  "access": { "read": ["Ledger.Read"], "write": ["Ledger.Write"] },
  "datatypes": {
    "email": { "maxLength": 128, "type": "System.String" },
    "guid": "System.Guid"
  },
  "name": "Ledger",
  "namespace": "AtlasLedger.Model",
  "tables": {
    "Person": {
      "columns": { "Email": "email?", "PersonId": "guid" },
      "key": ["PersonId"],
      "references": { "Node": { "columns": ["PersonId"] } }
    }
  },
  "version": "1.0"
}
```

- **`name`** names the generated root class (`Ledger`) and prefixes generated file names. It must not match a table name.
- **`version`** is the contract version, `major.minor`: the major number for breaking changes, the minor number for additive ones. The major number appears in the routes (`v1/...`).
- **Datatypes.** Every type the model uses is defined in `datatypes`; nothing is built in. An entry is a full CLR type name, or a domain that adds `maxLength`, `precision`, or `scale`. Enums and custom types are hand-written C#, resolved from the compilation; an unresolved type is a build error at its entry.
- **Keys** are always supplied by the client, never generated by the server, so data loads from and exports to JSON scripts with its relations exactly as written. If keys should be ordered (time-ordered `Guid`s, say), that's the client's choice. A `Guid` key of all zeros is refused, since it's almost always a key that was left out.
- **Columns** name a datatype entry. A trailing `?` makes a column nullable, as in C#; columns are non-nullable otherwise. Decimals must declare precision and scale; strings in a key or unique constraint must declare `maxLength`.
- **Tenancy.** Every table is tenant-scoped: it receives an implicit, immutable `TenantId`, keys are unique within the tenant, and the tenant is part of every reference. Every table also receives an implicit `RowVersion`. Tenants are flat; any grouping of tenants belongs to the control plane.
- **The built-in User table.** Every model has a `User` table supplied by Bellerophon: `UserId`, `IdentityIssuer`, `IdentitySubject` (unique within the tenant), `TenantId`, and `RowVersion`, with fixed scopes `Users.Read` and `Users.Write`. The identity columns are ordinary columns: required, unique together, and returned and synced like any other, so an exported user re-imports with its sign-in intact. Reading users requires `Users.Read`. `User` is a reserved name; put domain columns in a table that references it, like the model's `Member`.
- **`access`** lists the OAuth scopes that authorize reads and writes; the top-level value is the default and a table may override it.
- **`plural`.** The plural of a table's name follows regular English rules (`Account` → `Accounts`, `Entity` → `Entities`, `Address` → `Addresses`). Declare irregular ones: `"plural": "People"`.
- **`facts`.** `"facts": false` keeps a table out of the rules engine's working memory, saving memory per tenant for tables no rule reads. The built-in User table never enters it. Since only the server's engine sees facts, this property doesn't change the fingerprint.
- **`sync`.** `"sync": false` keeps a table out of the client data set. A synced table may not reference an unsynced one, so every reference on the client resolves.
- **Ordering.** Object properties are written in alphabetical order; arrays keep their written order (a key's order defines the index). The parser accepts any order and the model is canonicalized, so order never changes the generated code.
- Comments (`//`, `/* */`) and trailing commas are allowed.

## Generated code

**Both roles:**
- **The root class** (`Ledger`): the in-memory database, holding one partition per tenant (`GetPartition`, `FindPartition`, `DiscardPartition`), `TablesInDependencyOrder`, plus `ModelName`, `Version`, `Fingerprint`, `Role`, and `Tables`.
- **The partition** (`LedgerPartition`): one dictionary per table keyed by its key, one per unique constraint, and the operations that keep keys, unique constraints, references, and navigation consistent. Every change is made in a transaction (below). The tenant's lock, `SyncRoot`, is a `System.Threading.Lock`: exclusive and reentrant. Hold it (`lock (partition.SyncRoot) { ... }`) to walk the graph consistently.
- **The transaction** (`LedgerTransaction`): a unit of work in which every change commits together, or none does.
- **Rows**, with navigation in both directions:
  - toward the parent, named after the reference: `Account.Node`, `Edge.Parent`, `Member.Person` (null when the reference is unset);
  - toward the children, named after the child table's plural (`Node.Symbols`), or its singular when each parent has at most one (`Node.Account`, a one-to-one extension), and decorated with the reference's name when that differs from the parent's (`Node.EdgesByParent`), so adding a relationship never renames an existing property.

  Navigation follows object references: a child holds its parent, and a parent holds its children in a `HashSet`, allocated with its first child so rows without children carry no set. The dictionaries answer lookups by value.
- **One data transfer object per table,** `AccountDto`, in the model's root namespace (`AtlasLedger.Model.AccountDto`): every column, keys included, plus `RowVersion`, and never the tenant, which comes from the route. The same structure goes in both directions and appears in JSON scripts, so anything exported can be imported as it is. `RowVersion` is the row's current version when read; send it back unchanged with an update or delete; it's ignored on insert. Rows never go over the wire.

**Primary role, for the rules engine:**
- **Facts**, one per table (`AccountFact`, `PriceFact`): an immutable snapshot of a row, equal to any other fact with the same key, as a Rete engine requires to find a fact again on update or retraction. `RowVersion` rides along but isn't part of equality. Facts are `partial`, so rules code can add computed properties without touching the wire format.
- **The change feed** on each partition (`ReadChangesSince`, `TakeSnapshot`, `WaitForCommitAsync`) and its result, `LedgerChanges`, with `ToFacts()`.
- **In a project that references `Bellerophon.Rules`:** `LedgerRulesTenant`, which feeds a tenant's engine, and `LedgerRuleActions`, where rules record their decisions.

The replica role includes only synced tables, applies the server's data to its partition (`ApplyAccount`, `RemoveNode`; apply parents before children), and adds `Ledger.AddContractHeaders(headers)`.

**Server role only:**
- The transaction inserts, updates in place, and deletes (honoring each reference's delete rule: blocked while children exist, or cascading); updates and deletes name the version being replaced. A missing parent, a duplicate key or unique value, or a blocked delete throws `System.Data.ConstraintException`; a stale version throws `System.Data.DBConcurrencyException`.
- **Append-only tables** (`"writeMode": "AppendOnly"`: order intents, fills, violations) get inserts only, with no update or delete anywhere: partition, store, or controller.
- **Idempotent unique constraints** (`"idempotent": true`) make a duplicate insert return the row already stored instead of failing, so a retried order with the same client order id is never placed twice.
- **Latest-value tables** (`"concurrency": "LatestValue"`, which requires `"persistence": "Ephemeral"`) are lock-free stores of immutable records for prices and other ticks: `PublishPrice` replaces the previous record in one atomic step and raises `PricePublished`, a standard `EventHandler<Price>` event; `FindPrice` and `Prices` never wait. They are looked up by key, so they can neither reference nor be referenced, and they are never persisted, synced, or exposed through the API.
- For each table: an `I{Table}Store` interface and a `{Table}Controller` at `v{major}/tenants/{tenantId}/{table}`. A delete names the version being deleted: `DELETE .../{key}?rowVersion=7`. Constraint violations become 422 and stale versions 412.
- `LedgerBatchController`, applying several operations as one transaction (above).
- `LedgerStore`, implementing every store over the in-memory partitions; every write answers only once it's durable.
- The persistence hooks on `LedgerPartition`, and `LedgerCut` (below).
- `Ledger` implements `ITenantMembership` straight from each tenant's User table, so adding a user grants membership and removing one revokes it.
- `LedgerAccessPolicies`, `LedgerTenantsController` (`GET v{major}/me/tenants`), `UseLedgerContract()` (in `LedgerApplicationBuilderExtensions`), and `AddLedger()` (in `LedgerServiceCollectionExtensions`).

Hosting the primary role:

```csharp
builder.Services.AddLedger();       // the model, stores, membership, and access policies
builder.Services.AddControllers();
// ... JWT bearer authentication, including which token issuers to accept ...
application.UseLedgerContract();
application.UseAuthentication();
application.UseAuthorization();
application.MapControllers();
```

## Transactions

Every change to a partition is made in a transaction:

```csharp
var partition = ledger.GetPartition(tenantId);
using (var transaction = partition.BeginTransaction())
{
    var order = transaction.InsertOrder(new Order { /* ... */ });
    transaction.InsertExecution(new Execution { OrderId = order.OrderId, /* ... */ });
    transaction.UpdateOrder(changes);   // carries the version it read
    transaction.Commit();
}
// Disposed without Commit (an exception, a constraint, a stale version): every change is undone.
```

- **Isolation.** `BeginTransaction` takes the tenant's lock, and the transaction holds it until it commits or is disposed. It takes no other lock, so there are no lock upgrades, no lock ordering, and no deadlocks. Other threads wait; no one ever sees an uncommitted change. Only one transaction can be open on a partition.
- **Read your writes.** Changes take effect immediately, including indexes and navigation, so code inside the transaction sees them.
- **Atomicity.** Each change pushes one entry on the partition's single undo log; an update also keeps a shallow copy of the row's values. Disposing without committing replays the log newest first, restoring rows, versions, unique indexes, and links, and gives back the sequence numbers the undone changes took (so a later commit that changes nothing is durable at once), and only then releases the lock. A cascade pushes children before their parent, so undo restores the parent first.
- **Failed operations change nothing.** Every operation validates completely before it changes anything. After a constraint or version failure, the transaction stays open: continue, or dispose it to roll back.
- **Versions.** Every update and delete names the version it read; a stale version is refused.
- **Nothing outside the data model.** A transaction makes data-model changes only: no events, callbacks, or I/O. The transaction is a `ref struct`, so the compiler refuses to hold one across an `await` (error CS4007): put each transaction in a synchronous method. Anything that leaves the system, such as an order sent to a broker, happens after the commit, and responses from outside come back as new transactions.
- **Safety nets.** Copies of the transaction struct are harmless; its state lives in the partition. Changes cannot be made outside a transaction, a stored row cannot be passed as its own changes, and a row cannot move to another tenant. If a rollback itself ever failed, the partition would be marked faulted and refuse further use until reloaded from the database, which never sees uncommitted changes.
- **Durability.** A commit returns its sequence. Nothing outside the system learns of a change until that sequence is durable (see Persistence).
- **Shortcuts.** `partition.InsertOrder(order)` and the like open a transaction, make one change, and commit.
- **The client mirror** uses the same transactions (`ApplyAccount`, `RemoveNode`), so a sync client can apply each server transaction as a unit.

Clients commit several changes as a unit through the batch endpoint, `POST v{major}/tenants/{tenantId}/batch`:

```json
{
  "operations": [
    { "action": "Insert", "table": "Symbol", "row": { "scheme": "Custodian", "code": "2222", "nodeId": "..." } },
    { "action": "Update", "table": "Account", "key": { "accountId": "..." }, "row": { "accountId": "...", "accountType": "GroupAccount", "rowVersion": 7 } },
    { "action": "Delete", "table": "Symbol", "key": { "scheme": "Custodian", "code": "1234" }, "rowVersion": 5 }
  ]
}
```

Every table the batch writes is authorized first, every operation is read next, and only then are they applied in one transaction. The response holds one result per operation: the row as stored, or null for a delete. Any failure rolls back the whole batch: a constraint is 422, a stale version 412, a malformed batch 400. A batch holds its tenant's lock while it applies, so its size is limited: `Batch:MaximumOperations`, 10,000 by default (about 7 ms of lock time at the measured 0.6 µs per operation). A larger batch is refused with 413, changes nothing, and names the limit (`maximumOperations` in the problem response); the sample client's `ApplyInBatchesAsync` loads a large script in batches of at most that size, in order. Because the client supplies every key, one batch can insert a parent and its children together, for example a node and the account that extends it.

## Persistence

The primary role keeps its data in memory and persists it **write-behind**: transactions commit in memory, and a flusher writes them to the database afterward, outside the lock. Nothing leaves the system until it's durable, which is what makes that safe.

```csharp
builder.Services.AddSingleton(new Ledger { UsesWriteBehind = true });   // before AddLedger, which keeps it
builder.Services.AddLedger();
builder.Services.AddHostedService<MyFlusher>();
```

Without `UsesWriteBehind`, nothing is tracked and every commit is durable at once, which suits tests and tools.

**The hooks, per tenant partition:**
- **Commit** records each changed row in a dirty set, coalescing: several updates to a row become one write, and a row inserted and deleted before the next flush never reaches the database. It returns its sequence, and signals the flusher after the lock is released.
- **`WaitForChangesAsync`** wakes the flusher when there are changes.
- **`TakeCut()`** copies every changed row as data transfer objects into a `LedgerCut` and clears the changes. It's taken under the tenant's lock, so a cut always falls between transactions and holds only whole transactions.
- **Writing a cut** is the flusher's job, in one database transaction: the changed rows, the deleted rows, and the tenant's watermark, `cut.Sequence`. The database has primary keys only (the ledger enforces uniqueness and references; the loader checks them again on recovery), and a cut holds each key at most once, so the writes may run in any order. `Ledger.TablesInDependencyOrder` and each table's version order are still there for a writer that wants them. A project that references `Bellerophon.SqlServer` gets the writer generated (below).
- **`MarkDurable(sequence)`** advances the durable watermark and releases every writer waiting on it.
- **`WaitUntilDurableAsync(sequence)`** is how writers wait. The generated stores and the batch endpoint answer only once their commit is durable; code that commits its own transactions waits on the sequence `Commit()` returns.
- **`Fault(reason)`** is fail-stop. When the flusher can't persist, after retrying with backoff, it faults the partition: transactions are refused, waiting writers fail, and `Ledger.DiscardPartition` drops the tenant so it can be reloaded. Nothing faulted was ever acknowledged, synchronized, or sent to a broker, so callers simply retry.
- **`PendingChangeCount`** shows how far the flusher is behind, for monitoring and back-pressure.

**Recovery** rebuilds a tenant from the database into a fresh partition: `BeginLoad()`, then `Load{Table}(dto)` (or `LoadRow(dto)`, for loaders that hold rows without knowing their tables) for every row, parents first in `TablesInDependencyOrder`, then `CompleteLoad(watermark)`. Loading checks keys, unique values, and parents like an insert, but keeps each row's stored version and uses no undo log or change tracking. The partition then resumes after the watermark.

### SQL Server

A Primary project that references `Bellerophon.SqlServer` gets **`{Model}SqlDatabase`** (`LedgerSqlDatabase`), generated from the model:

- **Tables shared by every tenant.** Each table's primary key is `TenantId` followed by the model's key; there are no unique constraints, foreign keys, or secondary indexes. Columns use exact types: `nvarchar(n)` for strings with a `maxLength` (`nvarchar(max)` otherwise), `decimal(p,s)`, `uniqueidentifier`, `datetimeoffset(7)`, `bigint` for `RowVersion`, and so on. A durable column whose type has no SQL Server type is the build error `BEL2016` at its datatype.
- **`WriteAsync(cut)`** writes a cut in one round trip and one transaction (`SET XACT_ABORT ON`): for each table, an UPDATE then an INSERT from a table-valued parameter of the changed rows, a DELETE from one of the deleted keys, and the watermark in `[bellerophon].[Watermark]`. An overload takes the caller's connection and transaction and does not commit, so writes that must be durable with the cut (a broker connection's resume cursor) commit with it.
- **`LoadAsync(partition)`** loads a tenant in one round trip: the watermark, then every table in dependency order.
- **`PrepareAsync()`**, once at startup: checks that the migrations have been applied, and creates any missing table types (each named after a hash of its definition, so a changed table gets a new type).
- **Enumerations are stored as numbers**, the underlying values. Renumbering an enumeration is a data migration: export the tenants (scripts carry enumerations by name), change it, and load them again; or rewrite the stored numbers in SQL. Nothing checks the numbers at startup.
- **Values that would not fit are refused in memory.** A string or byte array longer than its `maxLength`, or a decimal outside its precision and scale, is a `ConstraintException` (422) on insert and update, so the database never receives one: a write that failed there would stop the tenant, and a decimal rounded there would differ from memory after a restart.

**The schema** is a separate project in the **Schema** role (`<BellerophonRole>Schema</BellerophonRole>`, referencing `Microsoft.EntityFrameworkCore.SqlServer`): it generates `{Model}DbContext` and one entity per durable table with the same column types, plus the watermark table, for migrations only. The tests check that the writer and the schema agree on every column. With the Entity Framework tools (`dotnet tool install --global dotnet-ef`):

```
dotnet ef migrations add <Name> --project samples/AtlasLedger.Schema     # after changing the model
dotnet ef database update --project samples/AtlasLedger.Schema           # apply to the database
```

`dotnet ef` reads the connection string from `LEDGER_SCHEMA_CONNECTION`, and uses `(localdb)\MSSQLLocalDB`, database `AtlasLedger`, when it is not set.

**The sample server** uses SQL Server when `ConnectionStrings:Ledger` is set, and keeps its database in memory otherwise. Either way, `LedgerRecovery`, the first hosted service, loads every tenant before the flusher, the compliance engine, or the API starts; a tenant that fails to load stops the server from starting.

**Retries are safe.** A retried update after a lost answer gets 412 (the version moved on), and re-reading shows the change applied. A retried insert whose row already exists with identical values returns the stored row; only a *different* row with the same key is refused.

**Visibility before durability.** Inside the system, a reader can see a committed change that a crash later erases. That reader was never promised durability, but API consumers should know it happens.

## The change feed

Each primary partition keeps a **journal** of committed changes, so readers can ask "what changed since sequence X?" The rules engine reads it now, and client sync will read it later.
- **Commit order.** Every change gets a sequence, deletions included, and is journaled at commit, so a reader never sees an uncommitted or rolled-back change, and never sees changes out of order.
- **Coalesced.** The journal keeps only a row's latest entry live, so it holds about one entry per changed row however often rows change. A read returns each changed row once, in its latest state, and each deleted key once. A key deleted and inserted again reads as changed.
- **`ReadChangesSince(sequence, throughSequence)`** returns the changes after the reader's sequence. The optional limit, for example the durable watermark for client sync, leaves rows whose latest change is past it for the next read, so nothing is skipped.
- **`TakeSnapshot()`** returns every row as of the last commit; a reader starts there, then reads changes since the snapshot's sequence. Nothing between the two can be lost.
- **`WaitForCommitAsync(sequence)`** wakes a reader at the next commit past its sequence. Every waiter shares one completion per commit, so waiting costs nothing that accumulates.
- **`TrimJournal(sequence)`** discards history no reader needs. A reader behind the horizon gets an incomplete read and starts again from a snapshot. Deleted rows stay in the journal until trimmed, so a host should trim periodically to the oldest sequence any reader still needs (for now, the rules engine's `AppliedSequence`).

## The compliance engine

The rules engine runs **one session per tenant**, each fed by its tenant's change feed and latest values:

1. **The first cycle** takes a snapshot, inserts every fact, and fires.
2. **Each later cycle** waits until the tenant changes (a commit, or a price), no sooner than `MinimumInterval` after the last (the cost control; changes in between coalesce). It then reads the changes since its position, applies them (insert, update, retract), and fires.
3. **The rules' decisions** are recorded in `LedgerRuleActions` while the rules fire, then committed after, **in one transaction**. They come back through the feed in the next cycle as facts, so rules see their own results, and well-written (idempotent) rules then settle.
4. **A refused decision** (a stale version or a broken constraint) commits nothing, and takes the cycle's other decisions with it. Since a Rete session fires a rule again only when its facts change, the next cycle rebuilds the session from a snapshot, so every rule decides again and no decision is lost. The first refusal is retried at once; repeated refusals back off from `RetryDelay`, doubling up to 32 times it, since they usually mean a rule is wrong (watch `RulesCycleResult.RejectedActions`). **A failed cycle** discards its decisions and rebuilds the session from a snapshot.

**Prices are conflated.** Publishing a price only records its key and wakes the engine; a cycle reads each changed key once, at its latest value. A burst of 1,000 ticks for a symbol becomes one fact update. Idle tenants cost nothing: their engines sleep.

Hosting it with NRules:

```csharp
// Compile your rules once; every tenant's session comes from them.
var repository = new RuleRepository();
repository.Load(spec => spec.From(typeof(MissingCustodianRule).Assembly));
var sessionFactory = new NRulesSessionFactory(repository.Compile());
builder.Services.AddSingleton(new RulesEngineHost(sessionFactory, new RulesEngineOptions { MinimumInterval = TimeSpan.FromMilliseconds(50) }));

// For each tenant's partition (when it's loaded):
host.Start(new LedgerRulesTenant(partition));
```

A rule receives its tenant's `LedgerRuleActions` as a dependency and records decisions there. It never writes the ledger itself. NRules compiles a rule's actions as expression trees, which can't contain the transaction (a `ref struct`), so the action calls an ordinary method that records the change:

```csharp
public class MissingCustodianRule : Rule
{
    public override void Define()
    {
        AccountFact account = default!;
        LedgerRuleActions actions = default!;
        Dependency().Resolve(() => actions);

        When()
            .Match(() => account)
            .Not<SymbolFact>(symbol => symbol.NodeId == account.AccountId, symbol => symbol.Scheme == "Custodian")
            .Not<AlertFact>(alert => alert.NodeId == account.AccountId, alert => alert.Rule == "MissingCustodian");
        Then()
            .Do(_ => RaiseAlert(actions, account.AccountId));
    }

    // Ordinary code, not an expression tree, so it can name the transaction.
    private static void RaiseAlert(LedgerRuleActions actions, Guid nodeId) =>
        actions.Add(transaction => transaction.InsertAlert(new Alert
        {
            AlertId = Guid.NewGuid(), NodeId = nodeId, Rule = "MissingCustodian", Message = "No custodian number.",
        }));
}
```

**Facts mirror the ledger; denormalized facts belong in the rules.** Generated facts are normalized, one per row. Shape working memory for matching with rules that join them and yield flattened facts, or with aggregates: NRules keeps those up to date as their sources change. Copy stable values (a security's sector) into derived facts; keep volatile ones (prices) as separate facts, so a tick updates one fact rather than every fact that copied it.

**Orders.** Each tick changes the value of positions and portfolios, and the rules decide what that means for orders: create one, amend one, or cancel one, all in the cycle's one transaction. Orders are mutable, with a status; cancelling is a status change, not a delete, so the history survives. Append-only tables suit records that never change once written, such as executions. An idempotent unique constraint, such as a client order id, makes a retried insert return the row already stored. Anything that leaves the system, such as an order sent to a broker, waits until the transaction is durable.

## Authorization

Authorization comes only from the token and the tenant's users, never from configuration a caller controls:

| Check | Fails with | Applies to |
|---|---|---|
| Authenticated by an accepted issuer | 401 | Everyone |
| A user of the tenant in the route (issuer and subject) | 404, the same as a tenant that does not exist | Callers acting for a person (delegated tokens, with `scp` or `scope`) |
| Holds one of the table's scopes | 403 | Everyone |

Tools acting as themselves skip the membership check, so bootstrap scripts and the operator tool can write to any tenant through the API, including the first users that make people members. A caller is a tool only if its token says so, and only if it is on the list:

- the token is an application's: `idtyp` is `app` (an optional claim to add to the API's app registration) and it carries no delegated scope;
- its client ID (`azp`, or `appid` in version 1.0 tokens) is in `Tools:ClientIds`.

Anything else, including a token that merely lacks scopes, is checked for membership like a person: fail closed. An empty list means no tools. The sample's `appsettings.json` ships placeholders (`<operator-tool-client-id>`, `<bootstrap-script-client-id>`) that match nothing and are reported at startup until replaced with the registrations' client IDs; the development tool is allowed only alongside development sign-in. Every tool access is logged (`Bellerophon.Runtime`, Information), since a tool can reach any tenant.

## The model contract

Server and client libraries both embed the model's version and fingerprint. The fingerprint hashes a compact canonical description of the model's meaning: every table (built-in ones included), column, key, reference, unique constraint, and policy, plus the members of every enumeration the model uses, read from the compiled code. Comments, descriptions, formatting, property order, and the order of enumeration members never change it. A client sends both with each request; if either differs from the server's, the server answers 426 Upgrade Required with its own values. The rule is an exact match for now; with schema evolution it relaxes to: same major version, and the client's minor version no higher than the server's.

## Naming

**The test: does the name document itself clearly, without arcane knowledge?** The reader is a .NET developer working in wealth management.

- **Abbreviations.** Use standard ones that this reader knows: .NET terms (`Id`, `Dto`, `Json`, `Http`, `Mvc`, `Clr`) and industry terms (`Kyc`, `Nav`, `Cusip`). Avoid abbreviations we make up, and names that need a key to read. Industry terms are explained in [GLOSSARY.md](GLOSSARY.md); add one there when you introduce it.
- **Acronym casing** follows .NET: two letters stay capitals (`IO`), longer acronyms are Pascal-cased (`AccountDto`, `XmlReader`, `KycReview`), and `Id` is treated as a word (`AccountId`).
- **Names fixed by others,** such as the `scp`, `oid`, and `iss` claims, appear only as values of named constants (`ObjectIdentifierClaim`), so the code reads without the specification at hand.
- **The .NET Framework Design Guidelines,** with StyleCop's field rules:
  - types, members, local functions, and namespaces are PascalCase; parameters and locals are camelCase;
  - constants, including local constants, and static readonly fields are PascalCase; other private fields are camelCase, without underscores; no field is visible outside its type;
  - interfaces start with `I`; exceptions end in `Exception`; attributes end in `Attribute`;
  - methods marked `async` end in `Async`, except test methods, which are named for the behavior they verify;
  - booleans read as true or false (`IsSynced`, `HasErrors`, `TryReadDigits`);
  - events use `EventHandler<T>` and are named for what happened (`PricePublished`);
  - extension classes are named for the type they extend (`LedgerServiceCollectionExtensions`, `LedgerApplicationBuilderExtensions`).
- **Generated names** follow the same rules: `Account` (the row), `AccountDto` (its data transfer object), `IAccountStore`, `AccountController`, `LedgerPartition`, `LedgerTransaction`.
- **Over the wire,** JSON properties are camelCase and routes are kebab-case (`order-intent`), as the web expects.

`.editorconfig` encodes the casing, prefix, and suffix rules, and `EnforceCodeStyleInBuild` reports violations as build warnings (IDE1006), in Visual Studio and on the command line alike. The rest of the guideline is for people: reviewers check abbreviations against the test above.
