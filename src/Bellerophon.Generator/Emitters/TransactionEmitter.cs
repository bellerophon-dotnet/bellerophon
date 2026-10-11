// <copyright file="TransactionEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Linq;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits the transaction: a <c>ref struct</c>, so the compiler refuses to hold one across an <c>await</c>, which
    /// guarantees nothing ever waits on I/O while holding a tenant's lock.  Its state lives in the partition, keyed by a
    /// token, so copies of the struct are harmless: committing one copy and disposing another does the right thing.
    /// </summary>
    internal static class TransactionEmitter
    {
        /// <summary>
        /// Emits the transaction.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <returns>The source text.</returns>
        public static string Emit(ModelShape shape)
        {
            var name = $"{shape.Name}Transaction";
            var partition = shape.PartitionName;
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary($"A unit of work on one tenant's {shape.Name} partition: every change commits together, or none does.");
            writer.Line("/// <remarks>");
            writer.Line("/// <para>Begin one with the partition's <c>BeginTransaction</c> and dispose it with <c>using</c>. It holds the tenant's lock until");
            writer.Line("/// it commits or is disposed; disposing it without committing undoes every change, newest first, before the lock is released.</para>");
            writer.Line("/// <para>A failed operation (a constraint, a stale version) changes nothing, and the transaction stays open: continue, or");
            writer.Line("/// dispose it to roll back. Make only data-model changes inside it: the compiler refuses to hold it across an await.</para>");
            writer.Line("/// </remarks>");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public readonly ref struct {name}");
            writer.Open();
            writer.Line($"private readonly {partition} partition;");
            writer.Line();
            writer.Line("private readonly long token;");
            writer.Line();
            writer.Line($"internal {name}({partition} partition, long token)");
            writer.Open();
            writer.Line("this.partition = partition;");
            writer.Line("this.token = token;");
            writer.Close();
            writer.Line();
            writer.Summary("Gets the partition, for reading within the transaction.");
            writer.Line($"public {partition} Partition => this.Open();");

            foreach (var table in shape.Tables.Where(table => table.Concurrency != ConcurrencyMode.LatestValue))
            {
                var row = shape.RowType(table);
                var durable = ModelShape.IsDurable(table);
                if (shape.IsPrimary)
                {
                    writer.Line();
                    writer.Summary($"Inserts a {table.Name}, with the key the caller supplies, and links it to its parents.");
                    writer.Line($"/// <exception cref=\"System.Data.ConstraintException\">A key is missing or taken, a unique value is taken, or a parent does not exist.</exception>");
                    writer.Line($"public {row} Insert{table.Name}({row} row) => this.Open().Insert{table.Name}Core(row);");
                    if (table.WriteMode != WriteMode.AppendOnly)
                    {
                        writer.Line();
                        writer.Summary($"Updates a {table.Name} in place from the key, values, and version in the changes.");
                        writer.Line("/// <returns>The updated row, or null if the tenant has no row with that key.</returns>");
                        writer.Line($"/// <exception cref=\"System.Data.DBConcurrencyException\">The row has changed since the version in the changes.</exception>");
                        writer.Line($"public {row}? Update{table.Name}({row} changes) => this.Open().Update{table.Name}Core(changes);");
                        writer.Line();
                        writer.Summary($"Deletes a {table.Name}, honoring each child relation's delete rule.");
                        writer.Line("/// <returns>False if the tenant has no row with that key.</returns>");
                        writer.Line($"public bool Delete{table.Name}({shape.KeyParameters(table)}{(durable ? ", long rowVersion" : string.Empty)}) => this.Open().Delete{table.Name}Core({shape.KeyParameterNames(table)}{(durable ? ", rowVersion" : string.Empty)});");
                    }
                }
                else
                {
                    writer.Line();
                    writer.Summary($"Applies the server's latest state of a {table.Name}. Apply parents before children.");
                    writer.Line($"public {row} Apply{table.Name}({shape.DtoType(table)} dto) => this.Open().Apply{table.Name}Core(dto);");
                    writer.Line();
                    writer.Summary($"Removes a {table.Name} and every row that depends on it.");
                    writer.Line($"public bool Remove{table.Name}({shape.KeyParameters(table)}) => this.Open().Remove{table.Name}Core({shape.KeyParameterNames(table)});");
                }
            }

            writer.Line();
            writer.Summary("Commits: keeps every change and releases the tenant's lock.");
            if (shape.IsPrimary)
            {
                writer.Line("/// <returns>The sequence to wait on (with the partition's WaitUntilDurableAsync) before telling anyone outside that the change happened.</returns>");
                writer.Line("public long Commit() => this.Open().Commit(this.token);");
            }
            else
            {
                writer.Line("public void Commit() => this.Open().Commit(this.token);");
            }
            writer.Line();
            writer.Summary("Ends the transaction: if it was not committed, undoes every change, newest first, then releases the lock.");
            writer.Line("public void Dispose() => this.partition?.End(this.token);");
            writer.Line();
            writer.Line($"private {partition} Open()");
            writer.Open();
            writer.Line($"var partition = this.partition ?? throw new global::System.InvalidOperationException(\"Begin a transaction with the partition's BeginTransaction.\");");
            writer.Line("partition.CheckOpen(this.token);");
            writer.Line("return partition;");
            writer.Close();
            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
