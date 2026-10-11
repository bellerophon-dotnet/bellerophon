// <copyright file="StoreImplementationEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Linq;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits the server's store: every table's store interface, implemented over the in-memory partitions.  Rows are
    /// copied to rows while the partition's lock is held, so a response is never a partly changed row.
    /// </summary>
    internal static class StoreImplementationEmitter
    {
        /// <summary>
        /// Emits the store.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <returns>The source text.</returns>
        public static string Emit(ModelShape shape)
        {
            var tables = shape.Tables.Where(table => table.Persistence == Persistence.Durable).ToList();
            var store = $"{shape.Name}Store";
            var task = "global::System.Threading.Tasks.Task";
            var token = "global::System.Threading.CancellationToken cancellationToken";
            var tenant = "global::System.Guid tenantId";

            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary($"Implements every {shape.Name} store over the in-memory partitions.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {store} : {string.Join(", ", tables.Select(table => $"I{table.Name}Store"))}");
            writer.Open();
            writer.Line($"private readonly {shape.RootType} model;");
            writer.Line();
            writer.Summary($"Initializes a new instance of the {store} class.");
            writer.Line("/// <param name=\"model\">The in-memory model.</param>");
            writer.Line($"public {store}({shape.RootType} model)");
            writer.Open();
            writer.Line("this.model = model;");
            writer.Close();

            foreach (var table in tables)
            {
                var row = shape.RowType(table);
                var dto = shape.DtoType(table);
                var key = shape.KeyParameters(table);
                var names = shape.KeyParameterNames(table);
                var iface = $"I{table.Name}Store";

                writer.Line();
                writer.Line($"{task}<global::System.Collections.Generic.IReadOnlyList<{dto}>> {iface}.ListAsync({tenant}, {token})");
                writer.Open();
                writer.Line($"var rows = new global::System.Collections.Generic.List<{dto}>();");
                writer.Line("if (this.model.FindPartition(tenantId) is { } partition)");
                writer.Open();
                writer.Line("lock (partition.SyncRoot)");
                writer.Open();
                writer.Line($"foreach (var row in partition.{Names.Identifier(table.Plural)})");
                writer.Open();
                writer.Line("rows.Add(row.ToDto());");
                writer.Close();
                writer.Close();
                writer.Close();
                writer.Line();
                writer.Line($"return {task}.FromResult<global::System.Collections.Generic.IReadOnlyList<{dto}>>(rows);");
                writer.Close();

                writer.Line();
                writer.Line($"{task}<{dto}?> {iface}.FindAsync({tenant}, {key}, {token})");
                writer.Open();
                writer.Line("if (this.model.FindPartition(tenantId) is { } partition)");
                writer.Open();
                writer.Line("lock (partition.SyncRoot)");
                writer.Open();
                writer.Line($"return {task}.FromResult(partition.Find{table.Name}({names})?.ToDto());");
                writer.Close();
                writer.Close();
                writer.Line();
                writer.Line($"return {task}.FromResult<{dto}?>(null);");
                writer.Close();

                writer.Line();
                writer.Line($"async {task}<{dto}> {iface}.InsertAsync({tenant}, {dto} dto, {token})");
                writer.Open();
                writer.Line("var partition = this.model.GetPartition(tenantId);");
                writer.Line($"{dto} stored;");
                writer.Line("long sequence;");
                writer.Line("lock (partition.SyncRoot)");
                writer.Open();
                writer.Line($"stored = partition.Insert{table.Name}({row}.FromInsert(dto)).ToDto();");
                writer.Line("sequence = partition.CommittedSequence;");
                writer.Close();
                writer.Line();
                writer.Line("// Answer only once the change is durable.");
                writer.Line("await partition.WaitUntilDurableAsync(sequence, cancellationToken).ConfigureAwait(false);");
                writer.Line("return stored;");
                writer.Close();

                if (table.WriteMode == WriteMode.AppendOnly)
                {
                    continue;
                }

                writer.Line();
                writer.Line($"async {task}<{dto}?> {iface}.UpdateAsync({tenant}, {key}, {dto} dto, {token})");
                writer.Open();
                writer.Line("if (this.model.FindPartition(tenantId) is not { } partition)");
                writer.Open();
                writer.Line("return null;");
                writer.Close();
                writer.Line();
                writer.Line($"{dto}? stored;");
                writer.Line("long sequence;");
                writer.Line("lock (partition.SyncRoot)");
                writer.Open();
                writer.Line($"stored = partition.Update{table.Name}({row}.FromUpdate(dto, {names}))?.ToDto();");
                writer.Line("sequence = partition.CommittedSequence;");
                writer.Close();
                writer.Line();
                writer.Line("await partition.WaitUntilDurableAsync(sequence, cancellationToken).ConfigureAwait(false);");
                writer.Line("return stored;");
                writer.Close();

                writer.Line();
                writer.Line($"async {task}<bool> {iface}.DeleteAsync({tenant}, {key}, long rowVersion, {token})");
                writer.Open();
                writer.Line("if (this.model.FindPartition(tenantId) is not { } partition)");
                writer.Open();
                writer.Line("return false;");
                writer.Close();
                writer.Line();
                writer.Line("bool deleted;");
                writer.Line("long sequence;");
                writer.Line("lock (partition.SyncRoot)");
                writer.Open();
                writer.Line($"deleted = partition.Delete{table.Name}({names}, rowVersion);");
                writer.Line("sequence = partition.CommittedSequence;");
                writer.Close();
                writer.Line();
                writer.Line("await partition.WaitUntilDurableAsync(sequence, cancellationToken).ConfigureAwait(false);");
                writer.Line("return deleted;");
                writer.Close();
            }

            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
