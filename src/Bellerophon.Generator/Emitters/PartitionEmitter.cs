// <copyright file="PartitionEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Linq;
    using Bellerophon.Modeling;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits a tenant's partition: its tables, their keys, unique constraints, and relations, and the transactions through
    /// which every change is made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dictionaries answer lookups by value.  Navigation follows object references: a child holds its parent, and a parent
    /// holds its children in a set allocated on the first child, so walking the graph never hashes a key.
    /// </para>
    /// <para>
    /// A transaction holds the tenant's lock from <c>BeginTransaction</c> until it commits or is disposed.  Every operation
    /// validates completely before changing anything, so a failed operation changes nothing; each change pushes one entry
    /// on the partition's single undo log.  Disposing without committing replays the log newest first, restoring rows,
    /// indexes, and links, and only then releases the lock, so no other thread ever sees an uncommitted change.  If the
    /// replay itself fails, the partition is marked faulted and refuses further use until it is reloaded.  Nothing outside
    /// the data model runs inside a transaction.  Latest-value tables are lock-free and stand outside transactions.
    /// </para>
    /// </remarks>
    internal static class PartitionEmitter
    {
        private const string Constraint = "global::System.Data.ConstraintException";

        private const string Concurrency = "global::System.Data.DBConcurrencyException";

        private const string InvalidOperation = "global::System.InvalidOperationException";

        /// <summary>
        /// Emits the partition.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <returns>The source text.</returns>
        public static string Emit(ModelShape shape)
        {
            var transactional = shape.Tables.Where(table => !IsLatestValue(table)).ToList();
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary($"One tenant's rows of the {shape.Name} model, with their keys, unique constraints, and relations. Every change is made in a transaction.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {shape.PartitionName}");
            writer.Open();
            for (var index = 0; index < transactional.Count; index++)
            {
                writer.Line($"private const int {TableId(transactional[index])} = {index};");
            }

            writer.Line("private const byte Inserted = 0;");
            writer.Line("private const byte Updated = 1;");
            writer.Line("private const byte Deleted = 2;");
            writer.Line();
            writer.Line("private readonly global::System.Threading.Lock syncRoot = new global::System.Threading.Lock();");
            writer.Line("private readonly global::System.Collections.Generic.List<UndoEntry> undoLog = new global::System.Collections.Generic.List<UndoEntry>();");
            writer.Line("private long openTransaction;");
            writer.Line("private long issuedTransactions;");
            writer.Line("private bool isFaulted;");
            if (shape.IsPrimary)
            {
                writer.Line("private readonly bool usesWriteBehind;");
                writer.Line("private readonly global::System.Collections.Generic.Dictionary<object, DirtyRow> dirtyRows = new global::System.Collections.Generic.Dictionary<object, DirtyRow>(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);");
                writer.Line("private readonly global::System.Threading.Lock durabilityGate = new global::System.Threading.Lock();");
                writer.Line("private readonly global::System.Collections.Generic.List<(long Sequence, global::System.Threading.Tasks.TaskCompletionSource Waiter)> durabilityWaiters = new global::System.Collections.Generic.List<(long Sequence, global::System.Threading.Tasks.TaskCompletionSource Waiter)>();");
                writer.Line("private global::System.Threading.Tasks.TaskCompletionSource? changeWaiter;");
                writer.Line("private global::System.Exception? durabilityFailure;");
                writer.Line("private long sequence;");
                writer.Line("private long committedSequence;");
                writer.Line("private long sequenceAtBegin;");
                writer.Line("private long durableSequence;");
                writer.Line("private bool isLoading;");
                writer.Line("private readonly global::System.Collections.Generic.List<JournalEntry> journal = new global::System.Collections.Generic.List<JournalEntry>();");
                writer.Line("private readonly global::System.Collections.Generic.Dictionary<object, int> journalPositions = new global::System.Collections.Generic.Dictionary<object, int>(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);");
                writer.Line("private int supersededJournalEntries;");
                writer.Line("private long journalHorizon;");
                writer.Line("private global::System.Threading.Tasks.TaskCompletionSource? commitWaiter;");
                writer.Line($"private static readonly bool[] PersistedTables = {{ {string.Join(", ", transactional.Select(table => ModelShape.IsDurable(table) ? "true" : "false"))} }};");
            }

            foreach (var table in shape.Tables)
            {
                var row = shape.RowType(table);
                var dictionary = IsLatestValue(table) ? "global::System.Collections.Concurrent.ConcurrentDictionary" : "global::System.Collections.Generic.Dictionary";
                writer.Line($"private readonly {dictionary}<{shape.KeyType(table)}, {row}> {shape.TableField(table)} = new {dictionary}<{shape.KeyType(table)}, {row}>();");
                foreach (var unique in shape.Uniques(table))
                {
                    var type = shape.CompositeType(table, unique.Columns);
                    writer.Line($"private readonly global::System.Collections.Generic.Dictionary<{type}, {row}> {shape.UniqueField(table, unique)} = new global::System.Collections.Generic.Dictionary<{type}, {row}>();");
                }
            }

            writer.Line();
            writer.Summary($"Initializes a new instance of the {shape.PartitionName} class.");
            writer.Line("/// <param name=\"tenantId\">The tenant.</param>");
            if (shape.IsPrimary)
            {
                writer.Line("/// <param name=\"usesWriteBehind\">Whether commits are kept for a write-behind flusher; without it, every commit counts as durable at once.</param>");
                writer.Line($"public {shape.PartitionName}(global::System.Guid tenantId, bool usesWriteBehind = false)");
                writer.Open();
                writer.Line("this.TenantId = tenantId;");
                writer.Line("this.usesWriteBehind = usesWriteBehind;");
                writer.Close();
            }
            else
            {
                writer.Line($"public {shape.PartitionName}(global::System.Guid tenantId)");
                writer.Open();
                writer.Line("this.TenantId = tenantId;");
                writer.Close();
            }
            writer.Line();
            writer.Summary("Gets the tenant.");
            writer.Line("public global::System.Guid TenantId { get; }");
            writer.Line();
            writer.Summary("Gets the tenant's lock. A transaction holds it; hold it yourself (with a lock statement) to walk the graph consistently. It is reentrant.");
            writer.Line("public global::System.Threading.Lock SyncRoot => this.syncRoot;");
            writer.Line();
            writer.Summary("Gets a value indicating whether the partition has stopped (a rollback failed, or its changes could not be made durable) and must be reloaded from the database.");
            writer.Line("public bool IsFaulted => this.isFaulted;");
            EmitTransactionControl(shape, transactional, writer);

            foreach (var table in shape.Tables)
            {
                if (IsLatestValue(table))
                {
                    EmitLatestValue(shape, table, writer);
                }
                else
                {
                    EmitTable(shape, table, writer);
                }
            }

            if (shape.IsPrimary)
            {
                PersistenceEmitter.EmitMembers(shape, transactional, writer);
            }

            writer.Line();
            writer.Summary("One change made by the open transaction, with what is needed to undo it.");
            writer.Line("private readonly struct UndoEntry");
            writer.Open();
            writer.Line("public UndoEntry(int table, byte change, object row, object? before, long sequence)");
            writer.Open();
            writer.Line("this.Table = table;");
            writer.Line("this.Change = change;");
            writer.Line("this.Row = row;");
            writer.Line("this.Before = before;");
            writer.Line("this.Sequence = sequence;");
            writer.Close();
            writer.Line();
            writer.Line("public int Table { get; }");
            writer.Line();
            writer.Line("public byte Change { get; }");
            writer.Line();
            writer.Line("public object Row { get; }");
            writer.Line();
            writer.Line("public object? Before { get; }");
            writer.Line();
            writer.Line("public long Sequence { get; }");
            writer.Close();
            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        private static bool IsLatestValue(TableModel table) => table.Concurrency == ConcurrencyMode.LatestValue;

        private static bool IsAppendOnly(TableModel table) => table.WriteMode == WriteMode.AppendOnly;

        private static string TableId(TableModel table) => $"{table.Name}Table";

        /// <summary>
        /// Gets whether the server can update and delete a table's rows directly (append-only rows are inserted once).
        /// </summary>
        private static bool Changeable(ModelShape shape, TableModel table) => !shape.IsPrimary || !IsAppendOnly(table);

        private static void EmitTransactionControl(ModelShape shape, List<TableModel> transactional, CodeWriter writer)
        {
            var transaction = $"{shape.Name}Transaction";
            writer.Line();
            writer.Summary("Begins a transaction: takes the tenant's lock, which the transaction holds until it commits or is disposed.");
            writer.Line("/// <returns>The transaction. Dispose it; without a commit, every change it made is undone.</returns>");
            writer.Line($"/// <exception cref=\"System.InvalidOperationException\">A transaction is already open on this partition, or the partition is faulted.</exception>");
            writer.Line($"public {transaction} BeginTransaction()");
            writer.Open();
            var loading = shape.IsPrimary ? " || this.isLoading" : string.Empty;
            writer.Line("this.syncRoot.Enter();");
            writer.Line($"if (this.isFaulted || this.openTransaction != 0{loading})");
            writer.Open();
            writer.Line("var message = this.isFaulted");
            writer.Line("    ? \"The partition is faulted. Discard it and reload the tenant from the database.\"");
            if (shape.IsPrimary)
            {
                writer.Line("    : this.isLoading ? \"The partition is loading; call CompleteLoad first.\"");
            }

            writer.Line("    : \"A transaction is already open on this partition; make every change through it.\";");
            writer.Line("this.syncRoot.Exit();");
            writer.Line($"throw new {InvalidOperation}(message);");
            writer.Close();
            writer.Line();
            writer.Line("this.openTransaction = ++this.issuedTransactions;");
            if (shape.IsPrimary)
            {
                writer.Line("this.sequenceAtBegin = this.sequence;");
            }

            writer.Line($"return new {transaction}(this, this.openTransaction);");
            writer.Close();

            writer.Line();
            writer.Summary("Ensures a transaction, identified by its token, is still open on the calling thread.");
            writer.Line("internal void CheckOpen(long token)");
            writer.Open();
            writer.Line("if (token == 0 || token != this.openTransaction || !this.syncRoot.IsHeldByCurrentThread)");
            writer.Open();
            writer.Line($"throw new {InvalidOperation}(\"The transaction has already ended.\");");
            writer.Close();
            writer.Close();

            writer.Line();
            writer.Summary("Commits a transaction: keeps its changes and releases the lock.");
            if (shape.IsPrimary)
            {
                writer.Line("/// <returns>The sequence to wait on for the commit to become durable.</returns>");
                writer.Line("internal long Commit(long token)");
                writer.Open();
                writer.Line("this.CheckOpen(token);");
                writer.Line("foreach (var entry in this.undoLog)");
                writer.Open();
                writer.Line("if (PersistedTables[entry.Table])");
                writer.Open();
                writer.Line("this.AppendToJournal(entry);");
                writer.Close();
                writer.Close();
                writer.Line();
                writer.Line("if (this.usesWriteBehind)");
                writer.Open();
                writer.Line("foreach (var entry in this.undoLog)");
                writer.Open();
                writer.Line("if (PersistedTables[entry.Table])");
                writer.Open();
                writer.Line("this.Track(entry);");
                writer.Close();
                writer.Close();
                writer.Close();
                writer.Line();
                writer.Line("var previous = this.committedSequence;");
                writer.Line("var committed = this.sequence;");
                writer.Line("global::System.Threading.Volatile.Write(ref this.committedSequence, committed);");
                writer.Line("if (!this.usesWriteBehind)");
                writer.Open();
                writer.Line("global::System.Threading.Volatile.Write(ref this.durableSequence, committed);");
                writer.Close();
                writer.Line();
                writer.Line("var hasChanges = this.dirtyRows.Count > 0;");
                writer.Line("this.undoLog.Clear();");
                writer.Line("this.openTransaction = 0;");
                writer.Line("this.syncRoot.Exit();");
                writer.Line();
                writer.Line("// After the lock is released: the transaction itself runs nothing outside the data model.");
                writer.Line("if (hasChanges)");
                writer.Open();
                writer.Line("this.SignalChanges();");
                writer.Close();
                writer.Line();
                writer.Line("if (committed > previous)");
                writer.Open();
                writer.Line("this.SignalCommit();");
                writer.Close();
                writer.Line();
                writer.Line("return committed;");
                writer.Close();
            }
            else
            {
                writer.Line("internal void Commit(long token)");
                writer.Open();
                writer.Line("this.CheckOpen(token);");
                writer.Line("this.undoLog.Clear();");
                writer.Line("this.openTransaction = 0;");
                writer.Line("this.syncRoot.Exit();");
                writer.Close();
            }

            writer.Line();
            writer.Summary("Ends a transaction that was not committed: undoes its changes, newest first, then releases the lock. Does nothing if it already ended.");
            writer.Line("internal void End(long token)");
            writer.Open();
            writer.Line("if (token == 0 || token != this.openTransaction || !this.syncRoot.IsHeldByCurrentThread)");
            writer.Open();
            writer.Line("return;");
            writer.Close();
            writer.Line();
            writer.Line("try");
            writer.Open();
            writer.Line("for (var index = this.undoLog.Count - 1; index >= 0; index--)");
            writer.Open();
            writer.Line("this.Undo(this.undoLog[index]);");
            writer.Close();
            if (shape.IsPrimary)
            {
                writer.Line();
                writer.Line("// Give back the sequences the undone changes took. Nothing outside the transaction saw them: they reach the");
                writer.Line("// journal and the dirty set only at commit. Otherwise a later commit that changes nothing would return a");
                writer.Line("// sequence no flush ever covers, and a writer waiting for it to become durable would wait indefinitely.");
                writer.Line("this.sequence = this.sequenceAtBegin;");
            }

            writer.Close();
            writer.Line("catch");
            writer.Open();
            writer.Line("this.isFaulted = true;");
            writer.Line("throw;");
            writer.Close();
            writer.Line("finally");
            writer.Open();
            writer.Line("this.undoLog.Clear();");
            writer.Line("this.openTransaction = 0;");
            writer.Line("this.syncRoot.Exit();");
            writer.Close();
            writer.Close();

            writer.Line();
            writer.Line("private void RequireTransaction()");
            writer.Open();
            writer.Line("if (this.openTransaction == 0 || !this.syncRoot.IsHeldByCurrentThread)");
            writer.Open();
            writer.Line($"throw new {InvalidOperation}(\"Changes are made only through a transaction: call BeginTransaction.\");");
            writer.Close();
            writer.Close();

            writer.Line();
            writer.Line("private void ThrowIfFaulted()");
            writer.Open();
            writer.Line("if (this.isFaulted)");
            writer.Open();
            writer.Line($"throw new {InvalidOperation}(\"The partition is faulted. Discard it and reload the tenant from the database.\");");
            writer.Close();
            writer.Close();

            writer.Line();
            writer.Line("private void Undo(UndoEntry entry)");
            writer.Open();
            writer.Line("switch (entry.Table)");
            writer.Open();
            foreach (var table in transactional)
            {
                var row = shape.RowType(table);
                writer.Line($"case {TableId(table)}:");
                writer.Line($"    if (entry.Change == Inserted) {{ this.UndoInsert{table.Name}(({row})entry.Row); return; }}");
                if (Changeable(shape, table))
                {
                    writer.Line($"    if (entry.Change == Updated) {{ this.UndoUpdate{table.Name}(({row})entry.Row, ({row})entry.Before!); return; }}");
                    writer.Line($"    if (entry.Change == Deleted) {{ this.UndoDelete{table.Name}(({row})entry.Row); return; }}");
                }

                writer.Line("    break;");
            }

            writer.Close();
            writer.Line();
            writer.Line($"throw new {InvalidOperation}($\"Unknown change {{entry.Change}} to table {{entry.Table}}.\");");
            writer.Close();
        }

        private static void EmitTable(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            var field = shape.TableField(table);
            writer.Line();
            writer.Summary($"Gets the {table.Plural}. Read it while holding the lock.");
            writer.Line($"public global::System.Collections.Generic.IReadOnlyCollection<{row}> {Names.Identifier(table.Plural)} => this.{field}.Values;");
            writer.Line();
            writer.Summary($"Finds a {table.Name} by its key.");
            writer.Line($"public {row}? Find{table.Name}({shape.KeyParameters(table)})");
            writer.Open();
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("this.ThrowIfFaulted();");
            writer.Line($"return this.{field}.TryGetValue({shape.KeyArguments(table)}, out var row) ? row : null;");
            writer.Close();
            writer.Close();

            foreach (var unique in shape.Uniques(table))
            {
                var parameters = string.Join(", ", unique.Columns.Select(name => $"{shape.NonNullableType(table.FindColumn(name)!)} {Names.Parameter(name)}"));
                var arguments = unique.Columns.Count == 1 ? Names.Parameter(unique.Columns[0]) : $"({string.Join(", ", unique.Columns.Select(Names.Parameter))})";
                writer.Line();
                writer.Summary($"Finds a {table.Name} by its {unique.Name} unique constraint.");
                writer.Line($"public {row}? Find{table.Name}By{unique.Name}({parameters})");
                writer.Open();
                writer.Line("lock (this.syncRoot)");
                writer.Open();
                writer.Line("this.ThrowIfFaulted();");
                writer.Line($"return this.{shape.UniqueField(table, unique)}.TryGetValue({arguments}, out var row) ? row : null;");
                writer.Close();
                writer.Close();
            }

            if (shape.IsPrimary)
            {
                EmitShortcuts(shape, table, writer);
                EmitInsert(shape, table, writer);
                if (Changeable(shape, table))
                {
                    EmitUpdate(shape, table, writer);
                    EmitDelete(shape, table, writer);
                }
            }
            else
            {
                EmitShortcuts(shape, table, writer);
                EmitApply(shape, table, writer);
                EmitRemove(shape, table, writer);
            }

            EmitUndo(shape, table, writer);
            foreach (var reference in table.References)
            {
                EmitRelationHelpers(shape, table, reference, writer);
            }
        }

        /// <summary>
        /// Emits the one-operation shortcuts: each opens a transaction, makes one change, and commits.
        /// </summary>
        private static void EmitShortcuts(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            var durable = ModelShape.IsDurable(table);
            var version = durable ? ", long rowVersion" : string.Empty;
            var versionArgument = durable ? ", rowVersion" : string.Empty;
            void Shortcut(string summary, string signature, string call)
            {
                writer.Line();
                writer.Summary(summary);
                writer.Line($"public {signature}");
                writer.Open();
                writer.Line("using var transaction = this.BeginTransaction();");
                writer.Line($"var result = transaction.{call};");
                writer.Line("transaction.Commit();");
                writer.Line("return result;");
                writer.Close();
            }

            if (shape.IsPrimary)
            {
                Shortcut($"Inserts a {table.Name} in a transaction of its own.", $"{row} Insert{table.Name}({row} row)", $"Insert{table.Name}(row)");
                if (Changeable(shape, table))
                {
                    Shortcut($"Updates a {table.Name} in a transaction of its own.", $"{row}? Update{table.Name}({row} changes)", $"Update{table.Name}(changes)");
                    Shortcut($"Deletes a {table.Name} in a transaction of its own.", $"bool Delete{table.Name}({shape.KeyParameters(table)}{version})", $"Delete{table.Name}({shape.KeyParameterNames(table)}{versionArgument})");
                }
            }
            else
            {
                Shortcut($"Applies the server's latest state of a {table.Name} in a transaction of its own.", $"{row} Apply{table.Name}({shape.DtoType(table)} dto)", $"Apply{table.Name}(dto)");
                Shortcut($"Removes a {table.Name}, and every row that depends on it, in a transaction of its own.", $"bool Remove{table.Name}({shape.KeyParameters(table)})", $"Remove{table.Name}({shape.KeyParameterNames(table)})");
            }
        }

        /// <summary>
        /// Gets the columns whose values have a size limit the database enforces: strings and byte arrays with a maximum
        /// length, and decimals with a precision.  The ledger refuses values outside them, so the database never sees one.
        /// </summary>
        private static IEnumerable<ColumnModel> SizedColumns(ModelShape shape, TableModel table) =>
            shape.DtoColumns(table).Where(column =>
                ((column.ClrType == "System.String" || column.ClrType == "System.Byte[]") && column.MaxLength.HasValue)
                || (column.ClrType == "System.Decimal" && column.Precision.HasValue));

        private static void EmitSizeCheck(ModelShape shape, TableModel table, CodeWriter writer)
        {
            writer.Line();
            writer.Summary($"Refuses a {table.Name} whose values do not fit their columns, before anything changes.");
            writer.Line($"private static void Check{table.Name}Sizes({shape.RowType(table)} row)");
            writer.Open();
            foreach (var column in SizedColumns(shape, table))
            {
                var property = $"row.{Names.Identifier(column.Name)}";
                if (column.ClrType == "System.Decimal")
                {
                    var precision = column.Precision!.Value;
                    var scale = column.Scale ?? 0;
                    var value = column.IsNullable ? $"{property}.Value" : property;
                    var guard = column.IsNullable ? $"{property}.HasValue && " : string.Empty;
                    var digits = precision - scale;
                    var limit = digits >= 29 ? null : "1" + new string('0', digits) + "m";
                    var tooLarge = limit == null ? "false" : $"global::System.Math.Abs(global::System.Decimal.Truncate({value})) >= {limit}";
                    writer.Line($"if ({guard}(global::System.Decimal.Round({value}, {scale}) != {value} || {tooLarge}))");
                    writer.Open();
                    writer.Line($"throw new {Constraint}($\"{table.Name}.{column.Name} is {{{value}}}, which does not fit decimal({precision},{scale}): at most {digits} digits before the point and {scale} after.\");");
                    writer.Close();
                }
                else
                {
                    var unit = column.ClrType == "System.String" ? "characters" : "bytes";
                    writer.Line($"if ({property}?.Length > {column.MaxLength})");
                    writer.Open();
                    writer.Line($"throw new {Constraint}($\"{table.Name}.{column.Name} is {{{property}.Length}} {unit}; the limit is {column.MaxLength}.\");");
                    writer.Close();
                }

                writer.Line();
            }

            writer.Close();
        }

        private static void EmitInsert(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            var field = shape.TableField(table);
            writer.Line();
            if (SizedColumns(shape, table).Any())
            {
                EmitSizeCheck(shape, table, writer);
            }

            writer.Line();
            writer.Line($"internal {row} Insert{table.Name}Core({row} row)");
            writer.Open();
            writer.Line("this.RequireTransaction();");
            NullCheck(writer, "row");
            if (SizedColumns(shape, table).Any())
            {
                writer.Line($"Check{table.Name}Sizes(row);");
            }

            writer.Line("if (row.TenantId != global::System.Guid.Empty && row.TenantId != this.TenantId)");
            writer.Open();
            writer.Line($"throw new {InvalidOperation}(\"The row belongs to another tenant.\");");
            writer.Close();
            writer.Line();
            foreach (var unique in shape.Uniques(table).Where(unique => unique.IsIdempotent))
            {
                var condition = shape.AllSet(table, unique.Columns, "row");
                var lookup = $"this.{shape.UniqueField(table, unique)}.TryGetValue({shape.CompositeValue(table, unique.Columns, "row")}, out var existing{unique.Name})";
                writer.Line($"// {unique.Name} is idempotent: inserting a duplicate returns the row already stored and changes nothing.");
                writer.Line(condition == "true" ? $"if ({lookup})" : $"if ({condition} && {lookup})");
                writer.Open();
                writer.Line($"return existing{unique.Name};");
                writer.Close();
                writer.Line();
            }

            foreach (var name in table.Key.Where(name => table.FindColumn(name)!.ClrType == "System.Guid"))
            {
                // The client always supplies the keys; an all-zero Guid is almost always a key that was left out.
                writer.Line($"if (row.{Names.Identifier(name)} == global::System.Guid.Empty)");
                writer.Open();
                writer.Line($"throw new {Constraint}(\"{table.Name}.{name} is missing: the client supplies every key, and an all-zero Guid is not a key.\");");
                writer.Close();
                writer.Line();
            }

            writer.Line($"var key = {shape.KeyValue(table, "row")};");
            writer.Line($"if (this.{field}.TryGetValue(key, out var existing))");
            writer.Open();
            writer.Line("// A retried insert finds the row already stored exactly as sent, and changes nothing.");
            writer.Line("if (existing.HasSameValues(row))");
            writer.Open();
            writer.Line("return existing;");
            writer.Close();
            writer.Line();
            writer.Line($"throw new {Constraint}($\"{table.Name} {{key}} already exists with different values.\");");
            writer.Close();
            writer.Line();
            CheckUniquesFree(shape, table, writer, "row", "row", insert: true);
            var parents = ResolveParents(shape, table, writer, "row", movableOnly: false);
            writer.Line("row.TenantId = this.TenantId;");
            if (ModelShape.IsDurable(table))
            {
                writer.Line("row.RowVersion = ++this.sequence;");
            }

            writer.Line($"this.{field}.Add(key, row);");
            AddUniques(shape, table, writer, "row");
            foreach (var (reference, variable) in parents)
            {
                writer.Line($"Link{shape.HelperName(table, reference)}(row, {variable});");
            }

            writer.Line($"this.undoLog.Add(new UndoEntry({TableId(table)}, Inserted, row, null, {(ModelShape.IsDurable(table) ? "row.RowVersion" : "0")}));");
            writer.Line("return row;");
            writer.Close();
        }

        private static void EmitUpdate(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            var field = shape.TableField(table);
            writer.Line();
            writer.Line($"internal {row}? Update{table.Name}Core({row} changes)");
            writer.Open();
            writer.Line("this.RequireTransaction();");
            NullCheck(writer, "changes");
            if (SizedColumns(shape, table).Any())
            {
                writer.Line($"Check{table.Name}Sizes(changes);");
            }

            writer.Line($"var key = {shape.KeyValue(table, "changes")};");
            writer.Line($"if (!this.{field}.TryGetValue(key, out var row))");
            writer.Open();
            writer.Line("return null;");
            writer.Close();
            writer.Line();
            writer.Line("if (object.ReferenceEquals(row, changes))");
            writer.Open();
            writer.Line($"throw new {InvalidOperation}(\"Pass the changes as a new row: the stored row must not be changed directly, or the change could not be undone.\");");
            writer.Close();
            writer.Line();
            if (ModelShape.IsDurable(table))
            {
                writer.Line("if (row.RowVersion != changes.RowVersion)");
                writer.Open();
                writer.Line($"throw new {Concurrency}($\"{table.Name} {{key}} is at version {{row.RowVersion}}, not {{changes.RowVersion}}; read it again and reapply the change.\");");
                writer.Close();
                writer.Line();
            }

            foreach (var column in shape.Declared(table).Where(column => column.IsImmutable && !shape.IsKeyColumn(table, column.Name)))
            {
                var name = Names.Identifier(column.Name);
                writer.Line($"if (!global::System.Collections.Generic.EqualityComparer<{shape.Type(column)}>.Default.Equals(row.{name}, changes.{name}))");
                writer.Open();
                writer.Line($"throw new {Constraint}(\"{table.Name}.{column.Name} cannot change after insert.\");");
                writer.Close();
                writer.Line();
            }

            CheckUniquesFree(shape, table, writer, "changes", "row", insert: false);
            var parents = ResolveParents(shape, table, writer, "changes", movableOnly: true);
            writer.Line("var before = row.CloneValues();");
            RemoveUniques(shape, table, writer, "row");
            foreach (var (reference, _) in parents)
            {
                writer.Line($"Unlink{shape.HelperName(table, reference)}(row);");
            }

            foreach (var column in shape.Mutable(table))
            {
                writer.Line($"row.{Names.Identifier(column.Name)} = changes.{Names.Identifier(column.Name)};");
            }

            if (ModelShape.IsDurable(table))
            {
                writer.Line("row.RowVersion = ++this.sequence;");
            }

            AddUniques(shape, table, writer, "row");
            foreach (var (reference, variable) in parents)
            {
                writer.Line($"Link{shape.HelperName(table, reference)}(row, {variable});");
            }

            writer.Line($"this.undoLog.Add(new UndoEntry({TableId(table)}, Updated, row, before, {(shape.IsPrimary && ModelShape.IsDurable(table) ? "row.RowVersion" : "0")}));");
            writer.Line("return row;");
            writer.Close();
        }

        private static void EmitDelete(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var field = shape.TableField(table);
            var durable = ModelShape.IsDurable(table);
            writer.Line();
            writer.Line($"internal bool Delete{table.Name}Core({shape.KeyParameters(table)}{(durable ? ", long rowVersion" : string.Empty)})");
            writer.Open();
            writer.Line("this.RequireTransaction();");
            writer.Line($"if (!this.{field}.TryGetValue({shape.KeyArguments(table)}, out var row))");
            writer.Open();
            writer.Line("return false;");
            writer.Close();
            writer.Line();
            if (durable)
            {
                writer.Line("if (row.RowVersion != rowVersion)");
                writer.Open();
                writer.Line($"throw new {Concurrency}($\"{table.Name} {{{shape.KeyArguments(table)}}} is at version {{row.RowVersion}}, not {{rowVersion}}; read it again before deleting it.\");");
                writer.Close();
                writer.Line();
            }

            writer.Line($"this.CheckDelete{table.Name}(row);");
            writer.Line($"this.Delete{table.Name}Row(row);");
            writer.Line("return true;");
            writer.Close();

            writer.Line();
            writer.Line($"private void CheckDelete{table.Name}({shape.RowType(table)} row)");
            writer.Open();
            foreach (var (child, reference) in shape.ChildrenOf(table))
            {
                var member = Navigation.ChildMemberName(child, reference);
                var oneToOne = Navigation.IsOneToOne(child, reference);
                if (reference.OnDelete == DeleteRule.Block)
                {
                    writer.Line(oneToOne ? $"if (row.{Names.Identifier(member)} != null)" : $"if (row.{Names.Identifier(member)}.Count > 0)");
                    writer.Open();
                    writer.Line($"throw new {Constraint}($\"{table.Name} {{{shape.KeyValue(table, "row")}}} still has {(oneToOne ? "its " + child.Name : child.Plural)} ({child.Name}.{reference.Name}); delete {(oneToOne ? "it" : "them")} first.\");");
                    writer.Close();
                }
                else if (oneToOne)
                {
                    writer.Line($"if (row.{Names.Identifier(member)} is {{ }} child{member})");
                    writer.Open();
                    writer.Line($"this.CheckDelete{child.Name}(child{member});");
                    writer.Close();
                }
                else
                {
                    writer.Line($"foreach (var child in row.{Names.Identifier(member)})");
                    writer.Open();
                    writer.Line($"this.CheckDelete{child.Name}(child);");
                    writer.Close();
                }
            }

            writer.Close();
            EmitRowRemoval(shape, table, writer, $"Delete{table.Name}Row", cascadeAll: false);
        }

        private static void EmitApply(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            var field = shape.TableField(table);
            writer.Line();
            writer.Line($"internal {row} Apply{table.Name}Core({shape.DtoType(table)} dto)");
            writer.Open();
            writer.Line("this.RequireTransaction();");
            NullCheck(writer, "dto");
            writer.Line($"var incoming = {row}.FromDto(dto);");
            writer.Line($"var key = {shape.KeyValue(table, "incoming")};");
            var parents = ResolveParents(shape, table, writer, "incoming", movableOnly: false);
            writer.Line($"if (this.{field}.TryGetValue(key, out var row))");
            writer.Open();
            writer.Line("var before = row.CloneValues();");
            RemoveUniques(shape, table, writer, "row");
            foreach (var (reference, _) in parents)
            {
                writer.Line($"Unlink{shape.HelperName(table, reference)}(row);");
            }

            foreach (var column in ClientChangeable(shape, table))
            {
                writer.Line($"row.{Names.Identifier(column.Name)} = incoming.{Names.Identifier(column.Name)};");
            }

            writer.Line("row.RowVersion = incoming.RowVersion;");
            AddUniques(shape, table, writer, "row");
            foreach (var (reference, variable) in parents)
            {
                writer.Line($"Link{shape.HelperName(table, reference)}(row, {variable});");
            }

            writer.Line($"this.undoLog.Add(new UndoEntry({TableId(table)}, Updated, row, before, {(shape.IsPrimary && ModelShape.IsDurable(table) ? "row.RowVersion" : "0")}));");
            writer.Line("return row;");
            writer.Close();
            writer.Line();
            writer.Line("incoming.TenantId = this.TenantId;");
            writer.Line($"this.{field}.Add(key, incoming);");
            AddUniques(shape, table, writer, "incoming");
            foreach (var (reference, variable) in parents)
            {
                writer.Line($"Link{shape.HelperName(table, reference)}(incoming, {variable});");
            }

            writer.Line($"this.undoLog.Add(new UndoEntry({TableId(table)}, Inserted, incoming, null, 0));");
            writer.Line("return incoming;");
            writer.Close();
        }

        private static void EmitRemove(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var field = shape.TableField(table);
            writer.Line();
            writer.Line($"internal bool Remove{table.Name}Core({shape.KeyParameters(table)})");
            writer.Open();
            writer.Line("this.RequireTransaction();");
            writer.Line($"if (!this.{field}.TryGetValue({shape.KeyArguments(table)}, out var row))");
            writer.Open();
            writer.Line("return false;");
            writer.Close();
            writer.Line();
            writer.Line($"this.Remove{table.Name}Row(row);");
            writer.Line("return true;");
            writer.Close();
            EmitRowRemoval(shape, table, writer, $"Remove{table.Name}Row", cascadeAll: true);
        }

        /// <summary>
        /// Emits the method that takes a row out of the partition: removes its children first (by cascade), then unlinks
        /// it from its parents and removes it from the unique and key dictionaries, pushing one undo entry per row.
        /// </summary>
        private static void EmitRowRemoval(ModelShape shape, TableModel table, CodeWriter writer, string method, bool cascadeAll)
        {
            var row = shape.RowType(table);
            var prefix = cascadeAll ? "Remove" : "Delete";
            writer.Line();
            writer.Line($"private void {method}({row} row)");
            writer.Open();
            foreach (var (child, reference) in shape.ChildrenOf(table))
            {
                if (!cascadeAll && reference.OnDelete == DeleteRule.Block)
                {
                    continue;
                }

                var member = Navigation.ChildMemberName(child, reference);
                if (Navigation.IsOneToOne(child, reference))
                {
                    writer.Line($"if (row.{Names.Identifier(member)} is {{ }} child{member})");
                    writer.Open();
                    writer.Line($"this.{prefix}{child.Name}Row(child{member});");
                    writer.Close();
                }
                else
                {
                    writer.Line($"if (row.{Names.Identifier(member)}.Count > 0)");
                    writer.Open();
                    writer.Line($"foreach (var child in new global::System.Collections.Generic.List<{shape.RowType(child)}>(row.{Names.Identifier(member)}))");
                    writer.Open();
                    writer.Line($"this.{prefix}{child.Name}Row(child);");
                    writer.Close();
                    writer.Close();
                }
            }

            foreach (var reference in table.References)
            {
                writer.Line($"Unlink{shape.HelperName(table, reference)}(row);");
            }

            RemoveUniques(shape, table, writer, "row");
            writer.Line($"this.{shape.TableField(table)}.Remove({shape.KeyValue(table, "row")});");
            writer.Line(shape.IsPrimary && ModelShape.IsDurable(table)
                ? $"this.undoLog.Add(new UndoEntry({TableId(table)}, Deleted, row, null, ++this.sequence));"
                : $"this.undoLog.Add(new UndoEntry({TableId(table)}, Deleted, row, null, 0));");
            writer.Close();
        }

        /// <summary>
        /// Emits the methods that undo one change.  They run newest first, so each finds the partition exactly as its change
        /// left it: an undone insert has no children left, and an undone delete's parents are back.
        /// </summary>
        private static void EmitUndo(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            var field = shape.TableField(table);

            writer.Line();
            writer.Line($"private void UndoInsert{table.Name}({row} row)");
            writer.Open();
            foreach (var reference in table.References)
            {
                writer.Line($"Unlink{shape.HelperName(table, reference)}(row);");
            }

            RemoveUniques(shape, table, writer, "row");
            writer.Line($"this.{field}.Remove({shape.KeyValue(table, "row")});");
            writer.Close();

            if (!Changeable(shape, table))
            {
                return;
            }

            var restored = shape.IsPrimary ? shape.Mutable(table).ToList() : ClientChangeable(shape, table).ToList();
            var relinked = table.References.Where(reference => !shape.IsPrimary || shape.IsMovable(table, reference)).ToList();
            writer.Line();
            writer.Line($"private void UndoUpdate{table.Name}({row} row, {row} before)");
            writer.Open();
            RemoveUniques(shape, table, writer, "row");
            foreach (var reference in relinked)
            {
                writer.Line($"Unlink{shape.HelperName(table, reference)}(row);");
            }

            foreach (var column in restored)
            {
                writer.Line($"row.{Names.Identifier(column.Name)} = before.{Names.Identifier(column.Name)};");
            }

            if (ModelShape.IsDurable(table))
            {
                writer.Line("row.RowVersion = before.RowVersion;");
            }

            AddUniques(shape, table, writer, "row");
            foreach (var reference in relinked)
            {
                writer.Line($"Link{shape.HelperName(table, reference)}(row, this.Resolve{shape.HelperName(table, reference)}(row));");
            }

            writer.Close();

            writer.Line();
            writer.Line($"private void UndoDelete{table.Name}({row} row)");
            writer.Open();
            writer.Line($"this.{field}.Add({shape.KeyValue(table, "row")}, row);");
            AddUniques(shape, table, writer, "row");
            foreach (var reference in table.References)
            {
                writer.Line($"Link{shape.HelperName(table, reference)}(row, this.Resolve{shape.HelperName(table, reference)}(row));");
            }

            writer.Close();
        }

        /// <summary>
        /// Gets the columns the client's partition changes when it applies a response: every non-key column it carries.
        /// </summary>
        private static IEnumerable<ColumnModel> ClientChangeable(ModelShape shape, TableModel table) =>
            shape.DtoColumns(table).Where(column => !shape.IsKeyColumn(table, column.Name));

        /// <summary>
        /// Emits a latest-value table: a lock-free store of immutable records, each replaced whole by publishing a new one.
        /// It stands outside transactions; readers never wait, and a burst of updates simply leaves the latest.
        /// </summary>
        private static void EmitLatestValue(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            var field = shape.TableField(table);
            writer.Line();
            writer.Summary($"Gets a snapshot of the latest {table.Plural}. Takes no lock.");
            writer.Line($"public global::System.Collections.Generic.IEnumerable<{row}> {Names.Identifier(table.Plural)} => this.{field}.Values;");
            writer.Line();
            writer.Summary($"Raised after a {table.Name} is published, so a reader (the rules engine) can mark it changed.");
            writer.Line($"public event global::System.EventHandler<{row}>? {table.Name}Published;");
            writer.Line();
            writer.Summary($"Raised after a {table.Name} is removed, with the record that was removed.");
            writer.Line($"public event global::System.EventHandler<{row}>? {table.Name}Removed;");
            writer.Line();
            writer.Summary($"Finds the latest {table.Name} by its key. Takes no lock.");
            writer.Line($"public {row}? Find{table.Name}({shape.KeyParameters(table)})");
            writer.Open();
            writer.Line($"return this.{field}.TryGetValue({shape.KeyArguments(table)}, out var row) ? row : null;");
            writer.Close();
            writer.Line();
            writer.Summary($"Publishes a {table.Name}, replacing the previous one with the same key in one atomic step. Takes no lock and is not part of any transaction.");
            writer.Line("/// <param name=\"row\">A new record; do not change it once published.</param>");
            writer.Line($"public void Publish{table.Name}({row} row)");
            writer.Open();
            NullCheck(writer, "row");
            writer.Line("row.TenantId = this.TenantId;");
            writer.Line($"this.{field}[{shape.KeyValue(table, "row")}] = row;");
            writer.Line($"this.{table.Name}Published?.Invoke(this, row);");
            writer.Close();
            writer.Line();
            writer.Summary($"Removes the latest {table.Name} with a key. Takes no lock.");
            writer.Line("/// <returns>False if there was none.</returns>");
            writer.Line($"public bool Remove{table.Name}({shape.KeyParameters(table)})");
            writer.Open();
            writer.Line($"if (!this.{field}.TryRemove({shape.KeyArguments(table)}, out var removed))");
            writer.Open();
            writer.Line("return false;");
            writer.Close();
            writer.Line();
            writer.Line($"this.{table.Name}Removed?.Invoke(this, removed);");
            writer.Line("return true;");
            writer.Close();
        }

        /// <summary>
        /// Emits, for one reference, the methods that find a child's parent and link or unlink the two.  A parent's set of
        /// children is allocated with its first child.
        /// </summary>
        private static void EmitRelationHelpers(ModelShape shape, TableModel child, ReferenceModel reference, CodeWriter writer)
        {
            var parent = shape.Table(reference.Table);
            var childRow = shape.RowType(child);
            var parentRow = shape.RowType(parent);
            var helper = shape.HelperName(child, reference);
            var navigation = Names.Identifier(reference.Name);
            var member = Navigation.ChildMemberName(child, reference);
            var oneToOne = Navigation.IsOneToOne(child, reference);
            var optional = shape.IsOptional(child, reference);

            writer.Line();
            writer.Line($"private {parentRow}? Resolve{helper}({childRow} row)");
            writer.Open();
            if (optional)
            {
                writer.Line($"if (!({shape.AllSet(child, reference.Columns, "row")}))");
                writer.Open();
                writer.Line("return null;");
                writer.Close();
                writer.Line();
            }

            writer.Line($"var parentKey = {shape.CompositeValue(child, reference.Columns, "row")};");
            writer.Line($"if (this.{shape.TableField(parent)}.TryGetValue(parentKey, out var parent))");
            writer.Open();
            writer.Line("return parent;");
            writer.Close();
            writer.Line();
            writer.Line($"throw new {Constraint}($\"{child.Name}.{reference.Name} refers to {parent.Name} {{parentKey}}, which does not exist in this tenant.\");");
            writer.Close();

            writer.Line();
            writer.Line($"private static void Link{helper}({childRow} row, {parentRow}? parent)");
            writer.Open();
            writer.Line(optional ? $"row.{navigation} = parent;" : $"row.{navigation} = parent!;");
            writer.Line("if (parent != null)");
            writer.Open();
            writer.Line(oneToOne
                ? $"parent.{Names.Identifier(member)} = row;"
                : $"parent.AddTo{member}(row);");
            writer.Close();
            writer.Close();

            writer.Line();
            writer.Line($"private static void Unlink{helper}({childRow} row)");
            writer.Open();
            writer.Line($"var parent = ({parentRow}?)row.{navigation};");
            writer.Line("if (parent != null)");
            writer.Open();
            writer.Line(oneToOne
                ? $"parent.{Names.Identifier(member)} = null;"
                : $"parent.RemoveFrom{member}(row);");
            writer.Close();
            writer.Line();
            writer.Line(optional ? $"row.{navigation} = null;" : $"row.{navigation} = null!;");
            writer.Close();
        }

        /// <summary>
        /// Emits the checks that a row's unique values are free (or already its own).  Idempotent constraints are handled
        /// before an insert by returning the stored row.
        /// </summary>
        internal static void CheckUniquesFree(ModelShape shape, TableModel table, CodeWriter writer, string source, string owner, bool insert)
        {
            foreach (var unique in shape.Uniques(table).Where(unique => !insert || !unique.IsIdempotent))
            {
                var condition = shape.AllSet(table, unique.Columns, source);
                var value = shape.CompositeValue(table, unique.Columns, source);
                var dictionary = shape.UniqueField(table, unique);
                var test = insert
                    ? $"this.{dictionary}.ContainsKey({value})"
                    : $"this.{dictionary}.TryGetValue({value}, out var holder{unique.Name}) && !object.ReferenceEquals(holder{unique.Name}, {owner})";
                writer.Line(condition == "true" ? $"if ({test})" : $"if ({condition} && {test})");
                writer.Open();
                writer.Line($"throw new {Constraint}($\"{table.Name}.{unique.Name} {{{value}}} is already taken.\");");
                writer.Close();
                writer.Line();
            }
        }

        internal static void AddUniques(ModelShape shape, TableModel table, CodeWriter writer, string row)
        {
            foreach (var unique in shape.Uniques(table))
            {
                var condition = shape.AllSet(table, unique.Columns, row);
                var statement = $"this.{shape.UniqueField(table, unique)}[{shape.CompositeValue(table, unique.Columns, row)}] = {row};";
                writer.Line(condition == "true" ? statement : $"if ({condition}) {{ {statement} }}");
            }
        }

        /// <summary>
        /// Emits the removal of a row's unique values, removing each only if it still maps to this row.
        /// </summary>
        private static void RemoveUniques(ModelShape shape, TableModel table, CodeWriter writer, string row)
        {
            foreach (var unique in shape.Uniques(table))
            {
                var condition = shape.AllSet(table, unique.Columns, row);
                var value = shape.CompositeValue(table, unique.Columns, row);
                var dictionary = shape.UniqueField(table, unique);
                var statement = $"if (this.{dictionary}.TryGetValue({value}, out var unique{unique.Name}Holder) && object.ReferenceEquals(unique{unique.Name}Holder, {row})) {{ this.{dictionary}.Remove({value}); }}";
                writer.Line(condition == "true" ? statement : $"if ({condition}) {{ {statement} }}");
            }
        }

        /// <summary>
        /// Emits the lookups of a row's parents, before anything changes, so a missing parent leaves the partition untouched.
        /// </summary>
        /// <returns>Each reference resolved and the variable holding its parent.</returns>
        internal static List<(ReferenceModel Reference, string Variable)> ResolveParents(
            ModelShape shape, TableModel table, CodeWriter writer, string row, bool movableOnly)
        {
            var parents = new List<(ReferenceModel Reference, string Variable)>();
            foreach (var reference in table.References)
            {
                if (movableOnly && !shape.IsMovable(table, reference))
                {
                    continue;
                }

                var variable = ModelShape.Field(reference.Name) + "Parent";
                writer.Line($"var {variable} = this.Resolve{shape.HelperName(table, reference)}({row});");
                parents.Add((reference, variable));
            }

            if (parents.Count > 0)
            {
                writer.Line();
            }

            return parents;
        }

        internal static void NullCheck(CodeWriter writer, string parameter)
        {
            writer.Line($"if ({parameter} is null)");
            writer.Open();
            writer.Line($"throw new global::System.ArgumentNullException(nameof({parameter}));");
            writer.Close();
            writer.Line();
        }
    }
}
