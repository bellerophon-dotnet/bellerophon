// <copyright file="SyncEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Linq;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits client sync: the wire types (both roles), the changes endpoint (server), and the sync client (Replica).  A client
    /// names the tables it needs; the server sends the rows changed since the client's sequence, up to the durable watermark,
    /// waiting for a commit when nothing has changed (long polling), or a snapshot when the client is new, behind the
    /// journal's horizon, or following an earlier epoch of the partition (a restarted server).
    /// </summary>
    internal static class SyncEmitter
    {
        private const string Lists = "global::System.Collections.Generic.IReadOnlyList";

        private const string Mvc = "global::Microsoft.AspNetCore.Mvc";

        private const string Task = "global::System.Threading.Tasks.Task";

        /// <summary>
        /// Gets the synced tables, parents first: the tables a client may ask for.
        /// </summary>
        public static List<TableModel> SyncedTables(ModelShape shape) =>
            shape.TablesInDependencyOrder().Where(table => table.IsSynced && ModelShape.IsModeled(table)).ToList();

        /// <summary>
        /// Emits the wire types and the table names, in the model's namespace (the same in both roles, like the transfer
        /// objects).
        /// </summary>
        public static string EmitTypes(ModelShape shape)
        {
            var tables = SyncedTables(shape);
            var name = shape.Name;
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.Namespace}");
            writer.Open();
            writer.Line("using global::System.Linq;");
            writer.Line();

            // The tables.
            writer.Summary($"The tables a client can sync from the {name} model, and their parents.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public static partial class {name}SyncTables");
            writer.Open();
            writer.Summary("Gets every table a client can sync, parents first.");
            writer.Line($"public static {Lists}<string> All {{ get; }} = new string[] {{ {string.Join(", ", tables.Select(table => $"\"{table.Name}\""))} }};");
            writer.Line();
            writer.Line("private static readonly global::System.Collections.Generic.Dictionary<string, string[]> Parents = new global::System.Collections.Generic.Dictionary<string, string[]>(global::System.StringComparer.OrdinalIgnoreCase)");
            writer.Open();
            foreach (var table in tables)
            {
                var parents = table.References.Select(reference => reference.Table).Where(parent => parent != table.Name).Distinct().Select(parent => $"\"{parent}\"");
                writer.Line($"[\"{table.Name}\"] = new string[] {{ {string.Join(", ", parents)} }},");
            }

            writer.Close();
            writer.Line(";");
            writer.Line();
            writer.Summary("Resolves the tables a view asks for: their names as the model spells them, with every parent added (a row's references must resolve in the Replica), parents first. Null or empty asks for every table.");
            writer.Line("/// <param name=\"requested\">The tables asked for.</param>");
            writer.Line("/// <param name=\"unknown\">The first name that is not a synced table, if any.</param>");
            writer.Line("/// <returns>The tables, parents first; empty if a name is unknown.</returns>");
            writer.Line($"public static {Lists}<string> Resolve(global::System.Collections.Generic.IEnumerable<string>? requested, out string? unknown)");
            writer.Open();
            writer.Line("unknown = null;");
            writer.Line("var names = requested?.Select(item => item.Trim()).Where(item => item.Length > 0).ToList();");
            writer.Line("if (names == null || names.Count == 0)");
            writer.Open();
            writer.Line("return All;");
            writer.Close();
            writer.Line();
            writer.Line("var wanted = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.OrdinalIgnoreCase);");
            writer.Line("var pending = new global::System.Collections.Generic.Stack<string>(names);");
            writer.Line("while (pending.Count > 0)");
            writer.Open();
            writer.Line("var table = pending.Pop();");
            writer.Line("if (!Parents.TryGetValue(table, out var parents))");
            writer.Open();
            writer.Line("unknown = table;");
            writer.Line("return global::System.Array.Empty<string>();");
            writer.Close();
            writer.Line();
            writer.Line("if (wanted.Add(table))");
            writer.Open();
            writer.Line("foreach (var parent in parents)");
            writer.Open();
            writer.Line("pending.Push(parent);");
            writer.Close();
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("return All.Where(wanted.Contains).ToList();");
            writer.Close();
            writer.Close();

            // A table's part of a response.
            writer.Line();
            writer.Summary("One table's part of a sync response: its rows changed (or, in a snapshot, every row), and its rows deleted.");
            writer.Line("/// <typeparam name=\"T\">The table's transfer object.</typeparam>");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {name}SyncTable<T>");
            writer.Open();
            writer.Summary("Gets or sets the rows inserted or changed, each in its latest state; in a snapshot, every row.");
            writer.Line($"public {Lists}<T> Rows {{ get; set; }} = global::System.Array.Empty<T>();");
            writer.Line();
            writer.Summary("Gets or sets the rows deleted, as they were when deleted (a client needs only their keys).");
            writer.Line($"public {Lists}<T> Deleted {{ get; set; }} = global::System.Array.Empty<T>();");
            writer.Close();

            // The tables of a response.
            writer.Line();
            writer.Summary("The tables of a sync response; a table not asked for, or unchanged, is null.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {name}SyncChanges");
            writer.Open();
            foreach (var table in tables)
            {
                writer.Summary($"Gets or sets the {table.Plural}.");
                writer.Line("[global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]");
                writer.Line($"public {name}SyncTable<{shape.DtoType(table)}>? {table.Name} {{ get; set; }}");
                writer.Line();
            }

            writer.Summary("Gets a value indicating whether no table has anything.");
            writer.Line("[global::System.Text.Json.Serialization.JsonIgnore]");
            writer.Line("public bool IsEmpty => " + (tables.Count == 0 ? "true" : string.Join(" && ", tables.Select(table => $"this.{table.Name} == null"))) + ";");
            writer.Close();

            // The response.
            writer.Line();
            writer.Summary("A sync response: the state of the tables asked for as of Through, as changes since the client's sequence, or as a snapshot (Reset).");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {name}SyncResponse");
            writer.Open();
            writer.Summary("Gets or sets the partition's epoch: send it back with the next request. A different epoch means the server restarted, and the response is a snapshot.");
            writer.Line("public global::System.Guid Epoch { get; set; }");
            writer.Line();
            writer.Summary("Gets or sets the sequence the response runs through (durable): the client's next since.");
            writer.Line("public long Through { get; set; }");
            writer.Line();
            writer.Summary("Gets or sets a value indicating whether the response is a snapshot: every row of the tables asked for. The client keeps exactly these rows of those tables.");
            writer.Line("public bool Reset { get; set; }");
            writer.Line();
            writer.Summary("Gets or sets the tables.");
            writer.Line($"public {name}SyncChanges Tables {{ get; set; }} = new {name}SyncChanges();");
            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        /// <summary>
        /// Emits the changes endpoint (server).
        /// </summary>
        public static string EmitController(ModelShape shape)
        {
            var tables = SyncedTables(shape);
            var name = shape.Name;
            var changes = $"{name}Changes";
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary("Client sync: the rows changed in the tables a client asks for since its sequence, up to the durable watermark; a snapshot for a new client, one behind the journal's horizon, or one following an earlier epoch. When nothing has changed it waits for a durable commit (long polling).");
            writer.Line("/// <remarks>");
            writer.Line($"/// <c>GET v{shape.Model.MajorVersion}/tenants/{{tenantId}}/changes?since={{sequence}}&amp;epoch={{epoch}}&amp;tables={{A,B}}&amp;wait={{seconds}}</c>.");
            writer.Line("/// Every table asked for (with its parents) needs its read scope: a person who isn't a member of the tenant gets 404, one");
            writer.Line("/// without a table's scope 403. Without a table list, every synced table the caller may read.");
            writer.Line("/// </remarks>");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"[{Mvc}.ApiController]");
            writer.Line("[global::Microsoft.AspNetCore.Authorization.Authorize]");
            writer.Line($"[{Mvc}.Route(\"v{shape.Model.MajorVersion}/tenants/{{tenantId:guid}}/changes\")]");
            writer.Line($"public partial class {name}ChangesController : {Mvc}.ControllerBase");
            writer.Open();
            writer.Line("private const int LongestWait = 60;");
            writer.Line();
            writer.Line($"private readonly {shape.RootType} ledger;");
            writer.Line();
            writer.Line("private readonly global::Microsoft.AspNetCore.Authorization.IAuthorizationService authorization;");
            writer.Line();
            writer.Summary($"Initializes a new instance of the {name}ChangesController class.");
            writer.Line("/// <param name=\"ledger\">The in-memory model.</param>");
            writer.Line("/// <param name=\"authorization\">Checks each table's read scope.</param>");
            writer.Line($"public {name}ChangesController({shape.RootType} ledger, global::Microsoft.AspNetCore.Authorization.IAuthorizationService authorization)");
            writer.Open();
            writer.Line("this.ledger = ledger;");
            writer.Line("this.authorization = authorization;");
            writer.Close();
            writer.Line();
            writer.Summary("Gets the changes.");
            writer.Line("/// <param name=\"tenantId\">The tenant.</param>");
            writer.Line("/// <param name=\"since\">The client's sequence: zero (or omitted) for a snapshot.</param>");
            writer.Line("/// <param name=\"epoch\">The epoch of the client's sequence.</param>");
            writer.Line("/// <param name=\"tables\">The tables, comma-separated; their parents are added.</param>");
            writer.Line("/// <param name=\"wait\">How long to wait for a change, in seconds (at most 60).</param>");
            writer.Line("/// <param name=\"cancellationToken\">Cancels the request.</param>");
            writer.Line("/// <returns>The changes.</returns>");
            writer.Line("[" + Mvc + ".HttpGet]");
            writer.Line($"public async {Task}<{Mvc}.ActionResult<global::{shape.Namespace}.{name}SyncResponse>> GetAsync(global::System.Guid tenantId, [{Mvc}.FromQuery] long since = 0, [{Mvc}.FromQuery] global::System.Guid? epoch = null, [{Mvc}.FromQuery] string? tables = null, [{Mvc}.FromQuery] int wait = 0, global::System.Threading.CancellationToken cancellationToken = default)");
            writer.Open();
            writer.Line("var asked = tables?.Split(',', global::System.StringSplitOptions.RemoveEmptyEntries | global::System.StringSplitOptions.TrimEntries);");
            writer.Line($"var requested = global::{shape.Namespace}.{name}SyncTables.Resolve(asked, out var unknown);");
            writer.Line("if (unknown != null)");
            writer.Open();
            writer.Line("return this.BadRequest($\"{unknown} is not a table that can be synced.\");");
            writer.Close();
            writer.Line();
            writer.Line("var allowed = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.Ordinal);");
            writer.Line("foreach (var table in requested)");
            writer.Open();
            writer.Line("var result = await this.authorization.AuthorizeAsync(this.User, tenantId, ReadPolicy(table)).ConfigureAwait(false);");
            writer.Line("if (result.Succeeded)");
            writer.Open();
            writer.Line("allowed.Add(table);");
            writer.Close();
            writer.Line("else if (global::Bellerophon.Runtime.TenantMembershipRequirement.IsMembershipFailure(result.Failure))");
            writer.Open();
            writer.Line("return this.NotFound();");
            writer.Close();
            writer.Line("else if (asked is { Length: > 0 })");
            writer.Open();
            writer.Line("return this.StatusCode(403);");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("if (allowed.Count == 0 || this.ledger.FindPartition(tenantId) is not { } partition)");
            writer.Open();
            writer.Line("return allowed.Count == 0 ? this.StatusCode(403) : this.NotFound();");
            writer.Close();
            writer.Line();
            writer.Line("var deadline = global::System.DateTimeOffset.UtcNow.AddSeconds(global::System.Math.Clamp(wait, 0, LongestWait));");
            writer.Line("while (true)");
            writer.Open();
            writer.Line("var reset = since <= 0 || epoch != partition.Epoch;");
            writer.Line($"{changes}? read = null;");
            writer.Line("if (!reset)");
            writer.Open();
            writer.Line("read = partition.ReadChangesSince(since, partition.DurableSequence);");
            writer.Line("reset = !read.IsComplete;");
            writer.Close();
            writer.Line();
            writer.Line("if (reset)");
            writer.Open();
            writer.Line("// A snapshot as of the last commit, sent once that commit is durable: nothing a crash could undo leaves.");
            writer.Line("read = partition.TakeSnapshot();");
            writer.Line("await partition.WaitUntilDurableAsync(read.ThroughSequence, cancellationToken).ConfigureAwait(false);");
            writer.Close();
            writer.Line();
            writer.Line("var response = Respond(read!, allowed, partition.Epoch, reset);");
            writer.Line("var remaining = deadline - global::System.DateTimeOffset.UtcNow;");
            writer.Line("if (reset || !response.Tables.IsEmpty || remaining <= global::System.TimeSpan.Zero)");
            writer.Open();
            writer.Line("return this.Ok(response);");
            writer.Close();
            writer.Line();
            writer.Line("since = response.Through;");
            writer.Line("using var timeout = global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);");
            writer.Line("timeout.CancelAfter(remaining);");
            writer.Line("try");
            writer.Open();
            writer.Line("await partition.WaitForCommitAsync(since, timeout.Token).ConfigureAwait(false);");
            writer.Line("await partition.WaitUntilDurableAsync(partition.CommittedSequence, timeout.Token).ConfigureAwait(false);");
            writer.Close();
            writer.Line("catch (global::System.OperationCanceledException) when (!cancellationToken.IsCancellationRequested)");
            writer.Open();
            writer.Line("return this.Ok(response);");
            writer.Close();
            writer.Close();
            writer.Close();
            writer.Line();

            // Building the response.
            writer.Line($"private static global::{shape.Namespace}.{name}SyncResponse Respond({changes} read, global::System.Collections.Generic.HashSet<string> tables, global::System.Guid epoch, bool reset)");
            writer.Open();
            writer.Line($"var response = new global::{shape.Namespace}.{name}SyncResponse {{ Epoch = epoch, Through = read.ThroughSequence, Reset = reset }};");
            foreach (var table in tables)
            {
                writer.Line($"if (tables.Contains(\"{table.Name}\") && (reset || read.Changed{table.Plural}.Count > 0 || read.Deleted{table.Plural}.Count > 0))");
                writer.Open();
                writer.Line($"response.Tables.{table.Name} = new global::{shape.Namespace}.{name}SyncTable<{shape.DtoType(table)}> {{ Rows = read.Changed{table.Plural}, Deleted = read.Deleted{table.Plural} }};");
                writer.Close();
            }

            writer.Line();
            writer.Line("return response;");
            writer.Close();
            writer.Line();
            writer.Line("private static string ReadPolicy(string table) => table switch");
            writer.Open();
            foreach (var table in tables)
            {
                writer.Line($"\"{table.Name}\" => {name}AccessPolicies.{table.Name}Read,");
            }

            writer.Line("_ => throw new global::System.ArgumentOutOfRangeException(nameof(table)),");
            writer.Close();
            writer.Line(";");
            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        /// <summary>
        /// Emits the sync client (Replica).
        /// </summary>
        public static string EmitClient(ModelShape shape)
        {
            var tables = SyncedTables(shape);
            var name = shape.Name;
            var response = $"global::{shape.Namespace}.{name}SyncResponse";
            var client = $"{name}SyncClient";
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Line("using global::System.Linq;");
            writer.Line();
            writer.Summary("What one sync applied to the Replica.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed class {name}SyncedEventArgs : global::System.EventArgs");
            writer.Open();
            writer.Summary("Initializes a new instance of the class.");
            writer.Line("/// <param name=\"tenantId\">The tenant.</param>");
            writer.Line("/// <param name=\"sequence\">The sequence the Replica is now at.</param>");
            writer.Line("/// <param name=\"isReset\">Whether it was a snapshot.</param>");
            writer.Line("/// <param name=\"tables\">The tables that changed.</param>");
            writer.Line($"public {name}SyncedEventArgs(global::System.Guid tenantId, long sequence, bool isReset, {Lists}<string> tables)");
            writer.Open();
            writer.Line("this.TenantId = tenantId;");
            writer.Line("this.Sequence = sequence;");
            writer.Line("this.IsReset = isReset;");
            writer.Line("this.Tables = tables;");
            writer.Close();
            writer.Line();
            writer.Summary("Gets the tenant.");
            writer.Line("public global::System.Guid TenantId { get; }");
            writer.Line();
            writer.Summary("Gets the sequence the Replica is now at.");
            writer.Line("public long Sequence { get; }");
            writer.Line();
            writer.Summary("Gets a value indicating whether it was a snapshot.");
            writer.Line("public bool IsReset { get; }");
            writer.Line();
            writer.Summary("Gets the tables that changed.");
            writer.Line($"public {Lists}<string> Tables {{ get; }}");
            writer.Close();

            writer.Line();
            writer.Summary("Sync refused by the server: the caller can't continue without a change (sign in again, another tenant, or a matching release).");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed class {name}SyncException : global::System.Exception");
            writer.Open();
            writer.Summary("Initializes a new instance of the class.");
            writer.Line("/// <param name=\"statusCode\">The server's answer.</param>");
            writer.Line("/// <param name=\"message\">What it means.</param>");
            writer.Line($"public {name}SyncException(global::System.Net.HttpStatusCode statusCode, string message)");
            writer.Line("    : base(message)");
            writer.Open();
            writer.Line("this.StatusCode = statusCode;");
            writer.Close();
            writer.Line();
            writer.Summary("Gets the server's answer: 401, 403, 404, or 426 (the client and server are built from different models).");
            writer.Line("public global::System.Net.HttpStatusCode StatusCode { get; }");
            writer.Close();

            writer.Line();
            writer.Summary("Keeps a tenant's tables in a Replica current with the server: a snapshot, then the rows changed since, each response applied in one transaction. A view model is a query over the Replica; Synced says when to re-run it.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {client}");
            writer.Open();
            writer.Line("private readonly global::System.Net.Http.HttpClient http;");
            writer.Line($"private readonly {shape.RootType} ledger;");
            writer.Line("private readonly global::System.Func<global::System.Net.Http.HttpRequestMessage, global::System.Threading.CancellationToken, global::System.Threading.Tasks.ValueTask> authorize;");
            writer.Line("private readonly global::System.Text.Json.JsonSerializerOptions json;");
            writer.Line("private readonly global::System.Threading.SemaphoreSlim gate = new global::System.Threading.SemaphoreSlim(1, 1);");
            writer.Line();
            writer.Summary($"Initializes a new instance of the {client} class.");
            writer.Line("/// <param name=\"http\">Reaches the server: its BaseAddress is the server's root.</param>");
            writer.Line("/// <param name=\"ledger\">The Replica the tables are kept in.</param>");
            writer.Line("/// <param name=\"tenantId\">The tenant.</param>");
            writer.Line("/// <param name=\"tables\">The tables the view needs (parents are added); null for every table.</param>");
            writer.Line("/// <param name=\"authorize\">Signs each request in: adds its bearer token, for example.</param>");
            writer.Line("/// <param name=\"json\">The server's JSON options (enums as names, for example); web defaults if null.</param>");
            writer.Line($"public {client}(global::System.Net.Http.HttpClient http, {shape.RootType} ledger, global::System.Guid tenantId, global::System.Collections.Generic.IEnumerable<string>? tables, global::System.Func<global::System.Net.Http.HttpRequestMessage, global::System.Threading.CancellationToken, global::System.Threading.Tasks.ValueTask> authorize, global::System.Text.Json.JsonSerializerOptions? json = null)");
            writer.Open();
            writer.Line("this.http = http ?? throw new global::System.ArgumentNullException(nameof(http));");
            writer.Line("this.ledger = ledger ?? throw new global::System.ArgumentNullException(nameof(ledger));");
            writer.Line("this.authorize = authorize ?? throw new global::System.ArgumentNullException(nameof(authorize));");
            writer.Line("this.json = json ?? new global::System.Text.Json.JsonSerializerOptions(global::System.Text.Json.JsonSerializerDefaults.Web);");
            writer.Line("this.TenantId = tenantId;");
            writer.Line($"this.Tables = global::{shape.Namespace}.{name}SyncTables.Resolve(tables, out var unknown);");
            writer.Line("if (unknown != null)");
            writer.Open();
            writer.Line("throw new global::System.ArgumentException($\"{unknown} is not a table that can be synced.\", nameof(tables));");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Summary("Raised after each sync that changed the Replica, outside its lock: re-run the view's queries.");
            writer.Line($"public event global::System.EventHandler<{name}SyncedEventArgs>? Synced;");
            writer.Line();
            writer.Summary("Gets the tenant.");
            writer.Line("public global::System.Guid TenantId { get; }");
            writer.Line();
            writer.Summary("Gets the tables kept, parents first.");
            writer.Line($"public {Lists}<string> Tables {{ get; }}");
            writer.Line();
            writer.Summary("Gets the sequence the Replica is at: zero before the first sync.");
            writer.Line("public long Sequence { get; private set; }");
            writer.Line();
            writer.Summary("Gets the server partition's epoch the sequence belongs to.");
            writer.Line("public global::System.Guid Epoch { get; private set; }");
            writer.Line();
            writer.Summary("Gets the Replica's partition for the tenant: read it (under its SyncRoot) to build a view.");
            writer.Line($"public {shape.PartitionName} Partition => this.ledger.GetPartition(this.TenantId);");
            writer.Line();
            writer.Summary("Syncs once: asks for the changes (waiting up to the given time for one), applies them, and raises Synced if anything changed.");
            writer.Line("/// <param name=\"wait\">How long the server may wait for a change (at most a minute); zero answers at once.</param>");
            writer.Line("/// <param name=\"cancellationToken\">Cancels the request.</param>");
            writer.Line("/// <returns>True if the Replica changed.</returns>");
            writer.Line($"/// <exception cref=\"{name}SyncException\">The server refused (401, 403, 404, 426).</exception>");
            writer.Line($"public async {Task}<bool> SyncAsync(global::System.TimeSpan wait, global::System.Threading.CancellationToken cancellationToken = default)");
            writer.Open();
            writer.Line("await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);");
            writer.Line("try");
            writer.Open();
            writer.Line("var seconds = (int)global::System.Math.Clamp(wait.TotalSeconds, 0, 60);");
            writer.Line($"var path = $\"v{shape.Model.MajorVersion}/tenants/{{this.TenantId}}/changes?since={{this.Sequence}}&epoch={{this.Epoch}}&tables={{string.Join(',', this.Tables)}}&wait={{seconds}}\";");
            writer.Line("using var request = new global::System.Net.Http.HttpRequestMessage(global::System.Net.Http.HttpMethod.Get, path);");
            writer.Line($"{shape.RootType}.AddContractHeaders(request.Headers);");
            writer.Line("await this.authorize(request, cancellationToken).ConfigureAwait(false);");
            writer.Line();
            writer.Line("using var answer = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);");
            writer.Line("switch ((int)answer.StatusCode)");
            writer.Open();
            writer.Line("case 401:");
            writer.Line($"    throw new {name}SyncException(answer.StatusCode, \"Not signed in, or the sign-in expired.\");");
            writer.Line("case 403:");
            writer.Line($"    throw new {name}SyncException(answer.StatusCode, \"Not permitted to read the tables asked for.\");");
            writer.Line("case 404:");
            writer.Line($"    throw new {name}SyncException(answer.StatusCode, \"Not a tenant the caller belongs to.\");");
            writer.Line("case 426:");
            writer.Line($"    throw new {name}SyncException(answer.StatusCode, \"The client and the server are built from different models: deploy them together.\");");
            writer.Close();
            writer.Line();
            writer.Line("answer.EnsureSuccessStatusCode();");
            writer.Line($"var body = await global::System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<{response}>(answer.Content, this.json, cancellationToken).ConfigureAwait(false)");
            writer.Line("    ?? throw new global::System.InvalidOperationException(\"The server's sync response was empty.\");");
            writer.Line("var changed = this.Apply(body);");
            writer.Line("this.Sequence = body.Through;");
            writer.Line("this.Epoch = body.Epoch;");
            writer.Line("if (changed.Count > 0 || body.Reset)");
            writer.Open();
            writer.Line($"this.Synced?.Invoke(this, new {name}SyncedEventArgs(this.TenantId, body.Through, body.Reset, changed));");
            writer.Close();
            writer.Line();
            writer.Line("return changed.Count > 0;");
            writer.Close();
            writer.Line("finally");
            writer.Open();
            writer.Line("this.gate.Release();");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Summary("Keeps the Replica current until cancelled: long polls, retrying network failures with a growing pause; a refusal (sign-in, access, release) ends it with the exception.");
            writer.Line("/// <param name=\"cancellationToken\">Stops syncing.</param>");
            writer.Line("/// <returns>A task that ends when cancelled, or fails with the server's refusal.</returns>");
            writer.Line($"public async {Task} RunAsync(global::System.Threading.CancellationToken cancellationToken)");
            writer.Open();
            writer.Line("var failures = 0;");
            writer.Line("while (!cancellationToken.IsCancellationRequested)");
            writer.Open();
            writer.Line("try");
            writer.Open();
            writer.Line("await this.SyncAsync(global::System.TimeSpan.FromSeconds(this.Sequence == 0 ? 0 : 30), cancellationToken).ConfigureAwait(false);");
            writer.Line("failures = 0;");
            writer.Close();
            writer.Line("catch (global::System.Exception exception) when (!cancellationToken.IsCancellationRequested && exception is global::System.Net.Http.HttpRequestException or global::System.Threading.Tasks.TaskCanceledException or global::System.Text.Json.JsonException)");
            writer.Open();
            writer.Line("failures++;");
            writer.Line($"await {Task}.Delay(global::System.TimeSpan.FromSeconds(global::System.Math.Min(30, 1 << global::System.Math.Min(failures, 5))), cancellationToken).ConfigureAwait(false);");
            writer.Close();
            writer.Close();
            writer.Close();
            writer.Line();

            // Applying a response.
            var ordered = tables;
            writer.Line($"private {Lists}<string> Apply({response} body)");
            writer.Open();
            writer.Line("var changed = new global::System.Collections.Generic.List<string>();");
            writer.Line("var partition = this.ledger.GetPartition(this.TenantId);");
            writer.Line("using var transaction = partition.BeginTransaction();");
            writer.Line("var tables = body.Tables;");
            writer.Line("// Rows parents first; then, children first, deletions, and in a snapshot every row the server no longer has.");
            foreach (var table in ordered)
            {
                writer.Line($"if (tables.{table.Name} is {{ }} {Names.Parameter(table.Plural)})");
                writer.Open();
                writer.Line($"foreach (var dto in {Names.Parameter(table.Plural)}.Rows)");
                writer.Open();
                writer.Line($"transaction.Apply{table.Name}(dto);");
                writer.Close();
                writer.Close();
            }

            writer.Line();
            for (var index = ordered.Count - 1; index >= 0; index--)
            {
                var table = ordered[index];
                var key = shape.KeyType(table);
                var keyOf = (string row) => table.Key.Count == 1 ? $"{row}.{Names.Identifier(table.Key[0])}" : $"({string.Join(", ", table.Key.Select(column => $"{row}.{Names.Identifier(column)}"))})";
                var keyArguments = (string row) => string.Join(", ", table.Key.Select(column => $"{row}.{Names.Identifier(column)}"));
                writer.Line($"if (tables.{table.Name} is {{ }} {Names.Parameter(table.Plural)}Gone)");
                writer.Open();
                writer.Line($"foreach (var dto in {Names.Parameter(table.Plural)}Gone.Deleted)");
                writer.Open();
                writer.Line($"transaction.Remove{table.Name}({keyArguments("dto")});");
                writer.Close();
                writer.Line();
                writer.Line("if (body.Reset)");
                writer.Open();
                writer.Line($"var kept = new global::System.Collections.Generic.HashSet<{key}>({Names.Parameter(table.Plural)}Gone.Rows.Select(dto => {keyOf("dto")}));");
                writer.Line($"foreach (var stale in partition.{Names.Identifier(table.Plural)}.Where(row => !kept.Contains({keyOf("row")})).ToList())");
                writer.Open();
                writer.Line($"transaction.Remove{table.Name}({keyArguments("stale")});");
                writer.Close();
                writer.Close();
                writer.Line();
                writer.Line($"if ({Names.Parameter(table.Plural)}Gone.Rows.Count > 0 || {Names.Parameter(table.Plural)}Gone.Deleted.Count > 0 || body.Reset)");
                writer.Open();
                writer.Line($"changed.Add(\"{table.Name}\");");
                writer.Close();
                writer.Close();
            }

            writer.Line();
            writer.Line("transaction.Commit();");
            writer.Line("changed.Reverse();");
            writer.Line("return changed;");
            writer.Close();
            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
