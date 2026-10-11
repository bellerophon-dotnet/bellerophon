// <copyright file="BatchEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Linq;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits the batch controller: <c>POST v{major}/tenants/{tenantId}/batch</c> applies an ordered list of operations in one
    /// transaction, so all of them commit or none does.
    /// </summary>
    /// <remarks>
    /// The work happens in three steps.  First, every table the batch writes is authorized (asynchronously, before any lock
    /// is taken).  Then every operation is read into a row or key (also before the lock).  Only then is the transaction
    /// begun, in a synchronous method, so the compiler guarantees it never spans an await.  Any failure rolls back the whole
    /// batch: a constraint becomes 422, a stale version 412, and a malformed batch 400.
    /// </remarks>
    internal static class BatchEmitter
    {
        private const string Mvc = "global::Microsoft.AspNetCore.Mvc";

        /// <summary>
        /// Emits the batch controller.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <returns>The source text.</returns>
        public static string Emit(ModelShape shape)
        {
            var tables = shape.Tables.Where(ModelShape.IsDurable).ToList();
            var controller = $"{shape.Name}BatchController";
            var policies = $"{shape.Name}AccessPolicies";
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary("Applies an ordered list of operations in one transaction: all of them commit, or none does.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"[{Mvc}.ApiController]");
            writer.Line("[global::Microsoft.AspNetCore.Authorization.Authorize]");
            writer.Line("[global::Bellerophon.Runtime.MapModelExceptions]");
            writer.Line($"[{Mvc}.Route(\"v{shape.Model.MajorVersion}/tenants/{{tenantId:guid}}/batch\")]");
            writer.Line($"public partial class {controller} : {Mvc}.ControllerBase");
            writer.Open();
            for (var index = 0; index < tables.Count; index++)
            {
                writer.Line($"private const int {tables[index].Name}Table = {index};");
            }

            writer.Line();
            writer.Line($"private readonly {shape.RootType} model;");
            writer.Line();
            writer.Line("private readonly global::Microsoft.AspNetCore.Authorization.IAuthorizationService authorization;");
            writer.Line();
            writer.Line("private readonly global::System.Text.Json.JsonSerializerOptions serializerOptions;");
            writer.Line();
            writer.Line("private readonly int maximumOperations;");
            writer.Line();
            writer.Summary($"Initializes a new instance of the {controller} class.");
            writer.Line("/// <param name=\"model\">The in-memory model.</param>");
            writer.Line("/// <param name=\"authorization\">Authorizes each table the batch writes.</param>");
            writer.Line("/// <param name=\"jsonOptions\">The API's JSON options, used to read each operation.</param>");
            writer.Line("/// <param name=\"batchOptions\">The limit on a batch's size, which bounds how long it holds the tenant's lock.</param>");
            writer.Line($"public {controller}({shape.RootType} model, global::Microsoft.AspNetCore.Authorization.IAuthorizationService authorization, global::Microsoft.Extensions.Options.IOptions<{Mvc}.JsonOptions> jsonOptions, global::Microsoft.Extensions.Options.IOptions<global::Bellerophon.Runtime.BatchOptions> batchOptions)");
            writer.Open();
            writer.Line("this.model = model;");
            writer.Line("this.authorization = authorization;");
            writer.Line("this.serializerOptions = jsonOptions.Value.JsonSerializerOptions;");
            writer.Line("this.maximumOperations = batchOptions.Value.MaximumOperations;");
            writer.Close();

            // The action.
            writer.Line();
            writer.Summary("Applies the batch. Returns one result per operation: the row as stored, or null for a delete.");
            writer.Line("[" + Mvc + ".HttpPost]");
            writer.Line($"public async global::System.Threading.Tasks.Task<{Mvc}.ActionResult<global::System.Collections.Generic.IReadOnlyList<object?>>> ApplyAsync(global::System.Guid tenantId, [{Mvc}.FromBody] global::Bellerophon.Runtime.BatchRequest request)");
            writer.Open();
            writer.Line("if (request is null || request.Operations.Count == 0)");
            writer.Open();
            writer.Line("return this.BadRequest();");
            writer.Close();
            writer.Line();
            writer.Line("// A batch holds the tenant's lock while it applies, so its size is limited; a larger one changes nothing.");
            writer.Line("if (request.Operations.Count > this.maximumOperations)");
            writer.Open();
            writer.Line($"var problem = new {Mvc}.ProblemDetails {{ Status = global::Microsoft.AspNetCore.Http.StatusCodes.Status413PayloadTooLarge, Title = \"The batch has too many operations.\" }};");
            writer.Line("problem.Detail = $\"The batch has {request.Operations.Count} operations; the limit is {this.maximumOperations}. Send them in several batches.\";");
            writer.Line("problem.Extensions[\"maximumOperations\"] = this.maximumOperations;");
            writer.Line("return this.StatusCode(global::Microsoft.AspNetCore.Http.StatusCodes.Status413PayloadTooLarge, problem);");
            writer.Close();
            writer.Line();
            writer.Line("// Authorize every table the batch writes before anything changes.");
            writer.Line("var authorized = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.Ordinal);");
            writer.Line("foreach (var operation in request.Operations)");
            writer.Open();
            writer.Line("if (authorized.Add(operation.Table))");
            writer.Open();
            writer.Line("var result = await this.authorization.AuthorizeAsync(this.User, this.HttpContext, WritePolicy(operation.Table)).ConfigureAwait(false);");
            writer.Line("if (!result.Succeeded)");
            writer.Open();
            writer.Line("return global::Bellerophon.Runtime.TenantMembershipRequirement.IsMembershipFailure(result.Failure) ? this.NotFound() : this.Forbid();");
            writer.Close();
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("// Read every operation before taking the lock.");
            writer.Line("var prepared = new global::System.Collections.Generic.List<Prepared>(request.Operations.Count);");
            writer.Line("foreach (var operation in request.Operations)");
            writer.Open();
            writer.Line("prepared.Add(this.Prepare(operation));");
            writer.Close();
            writer.Line();
            writer.Line("var partition = this.model.GetPartition(tenantId);");
            writer.Line("var (results, sequence) = Apply(partition, prepared);");
            writer.Line();
            writer.Line("// Answer only once the whole batch is durable.");
            writer.Line("await partition.WaitUntilDurableAsync(sequence, this.HttpContext.RequestAborted).ConfigureAwait(false);");
            writer.Line("return this.Ok(results);");
            writer.Close();

            // The write policy for each table.
            writer.Line();
            writer.Line("private static string WritePolicy(string table)");
            writer.Open();
            writer.Line("return table switch");
            writer.Open();
            foreach (var table in tables)
            {
                writer.Line($"\"{table.Name}\" => {policies}.{table.Name}Write,");
            }

            writer.Line("_ => throw new global::Bellerophon.Runtime.InvalidBatchException($\"There is no table '{table}' to write.\"),");
            writer.Close();
            writer.Line(";");
            writer.Close();

            EmitPrepare(shape, tables, writer);
            EmitApply(shape, tables, writer);

            writer.Line();
            writer.Line("private static global::System.Data.ConstraintException Missing(string table, object key)");
            writer.Open();
            writer.Line("return new global::System.Data.ConstraintException($\"The batch refers to {table} {key}, which does not exist in this tenant.\");");
            writer.Close();
            writer.Line();
            writer.Line("private readonly record struct Prepared(int Table, global::Bellerophon.Runtime.BatchAction Action, object? Row, object? Key, long RowVersion);");
            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        /// <summary>
        /// Emits the step that reads one operation into a row (insert, update) or a key (delete), outside the lock.
        /// </summary>
        private static void EmitPrepare(ModelShape shape, List<TableModel> tables, CodeWriter writer)
        {
            const string Reader = "global::Bellerophon.Runtime.BatchReader";
            const string Action = "global::Bellerophon.Runtime.BatchAction";
            writer.Line();
            writer.Line("private Prepared Prepare(global::Bellerophon.Runtime.BatchOperation operation)");
            writer.Open();
            writer.Line("switch (operation.Table)");
            writer.Open();
            foreach (var table in tables)
            {
                var row = shape.QualifiedRowType(table);
                var keyReads = table.Key.Select(name => $"{Reader}.Key<{shape.NonNullableType(table.FindColumn(name)!)}>(operation, \"{name}\", this.serializerOptions)").ToList();
                var key = keyReads.Count == 1 ? keyReads[0] : $"({string.Join(", ", keyReads)})";
                writer.Line($"case \"{table.Name}\":");
                writer.Line($"    if (operation.Action == {Action}.Insert)");
                writer.Line("    {");
                writer.Line($"        return new Prepared({table.Name}Table, operation.Action, {row}.FromInsert({Reader}.Row<{shape.DtoType(table)}>(operation, this.serializerOptions)), null, 0);");
                writer.Line("    }");
                if (table.WriteMode != WriteMode.AppendOnly)
                {
                    writer.Line();
                    writer.Line($"    if (operation.Action == {Action}.Update)");
                    writer.Line("    {");
                    writer.Line($"        return new Prepared({table.Name}Table, operation.Action, {row}.FromUpdate({Reader}.Row<{shape.DtoType(table)}>(operation, this.serializerOptions), {string.Join(", ", keyReads)}), null, 0);");
                    writer.Line("    }");
                    writer.Line();
                    writer.Line($"    if (operation.Action == {Action}.Delete)");
                    writer.Line("    {");
                    writer.Line($"        return new Prepared({table.Name}Table, operation.Action, null, {key}, operation.RowVersion);");
                    writer.Line("    }");
                }

                writer.Line();
                writer.Line("    break;");
            }

            writer.Close();
            writer.Line();
            writer.Line("throw new global::Bellerophon.Runtime.InvalidBatchException($\"{operation.Action} is not allowed on {operation.Table}.\");");
            writer.Close();
        }

        /// <summary>
        /// Emits the step that applies every prepared operation in one transaction.  It is synchronous, so the compiler
        /// refuses any await while the transaction (and the tenant's lock) is held.  Data transfer objects are built inside the
        /// transaction, so none is a partly changed row.
        /// </summary>
        private static void EmitApply(ModelShape shape, List<TableModel> tables, CodeWriter writer)
        {
            const string Action = "global::Bellerophon.Runtime.BatchAction";
            writer.Line();
            writer.Line($"private static (global::System.Collections.Generic.List<object?> Results, long Sequence) Apply({shape.PartitionName} partition, global::System.Collections.Generic.List<Prepared> prepared)");
            writer.Open();
            writer.Line("var results = new global::System.Collections.Generic.List<object?>(prepared.Count);");
            writer.Line("using var transaction = partition.BeginTransaction();");
            writer.Line("foreach (var operation in prepared)");
            writer.Open();
            writer.Line("switch (operation.Table)");
            writer.Open();
            foreach (var table in tables)
            {
                var row = shape.QualifiedRowType(table);
                writer.Line($"case {table.Name}Table:");
                writer.Line($"    if (operation.Action == {Action}.Insert)");
                writer.Line("    {");
                writer.Line($"        results.Add(transaction.Insert{table.Name}(({row})operation.Row!).ToDto());");
                writer.Line("    }");
                if (table.WriteMode != WriteMode.AppendOnly)
                {
                    var keyType = shape.KeyType(table);
                    var keyArguments = table.Key.Count == 1
                        ? "key"
                        : string.Join(", ", Enumerable.Range(1, table.Key.Count).Select(index => $"key.Item{index}"));
                    writer.Line($"    else if (operation.Action == {Action}.Update)");
                    writer.Line("    {");
                    writer.Line($"        var changes = ({row})operation.Row!;");
                    writer.Line($"        results.Add((transaction.Update{table.Name}(changes) ?? throw Missing(\"{table.Name}\", {shape.KeyValue(table, "changes")})).ToDto());");
                    writer.Line("    }");
                    writer.Line("    else");
                    writer.Line("    {");
                    writer.Line($"        var key = ({keyType})operation.Key!;");
                    writer.Line($"        if (!transaction.Delete{table.Name}({keyArguments}, operation.RowVersion))");
                    writer.Line("        {");
                    writer.Line($"            throw Missing(\"{table.Name}\", key);");
                    writer.Line("        }");
                    writer.Line();
                    writer.Line("        results.Add(null);");
                    writer.Line("    }");
                }

                writer.Line();
                writer.Line("    break;");
            }

            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("return (results, transaction.Commit());");
            writer.Close();
        }
    }
}
