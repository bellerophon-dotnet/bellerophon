// <copyright file="PersistenceEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Linq;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits the primary partition's persistence hooks and the cut a write-behind flusher receives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With write-behind on, commit records each changed row in a dirty set, coalescing: several updates to a row become one
    /// change, and a row inserted and deleted between flushes disappears.  The flusher waits for changes, takes a cut (a copy
    /// of every dirty row, taken under the lock, so it always falls between transactions), writes it in one database
    /// transaction with the tenant's watermark, and marks the cut durable, which releases the writers waiting on it.  If the
    /// flusher cannot persist, it faults the partition: writes stop, waiters fail, and the tenant is reloaded.
    /// </para>
    /// <para>
    /// Without write-behind, nothing is tracked and every commit is durable at once.  Recovery loads a fresh partition
    /// from the database, parents first, with no undo log and no change tracking.
    /// </para>
    /// </remarks>
    internal static class PersistenceEmitter
    {
        private const string Task = "global::System.Threading.Tasks.Task";

        private const string Completion = "global::System.Threading.Tasks.TaskCompletionSource";

        private const string Volatile = "global::System.Threading.Volatile";

        private const string InvalidOperation = "global::System.InvalidOperationException";

        private const string Constraint = "global::System.Data.ConstraintException";

        /// <summary>
        /// Emits the persistence members of the primary partition.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <param name="transactional">The tables under the partition's lock, in table-id order.</param>
        /// <param name="writer">The writer.</param>
        public static void EmitMembers(ModelShape shape, List<TableModel> transactional, CodeWriter writer)
        {
            var durable = transactional.Where(ModelShape.IsDurable).ToList();
            var cut = $"{shape.Name}Cut";

            writer.Line();
            writer.Summary("Gets the sequence of the last committed transaction.");
            writer.Line($"public long CommittedSequence => {Volatile}.Read(ref this.committedSequence);");
            writer.Line();
            writer.Summary("Gets the durable watermark: every transaction up to this sequence is in the database.");
            writer.Line($"public long DurableSequence => {Volatile}.Read(ref this.durableSequence);");
            writer.Line();
            writer.Summary("Gets a value indicating whether commits are kept for a write-behind flusher.");
            writer.Line("public bool UsesWriteBehind => this.usesWriteBehind;");
            writer.Line();
            writer.Summary("Gets the number of changed rows waiting for the flusher, for back-pressure and monitoring.");
            writer.Line("public int PendingChangeCount");
            writer.Open();
            writer.Line("get");
            writer.Open();
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("return this.dirtyRows.Count;");
            writer.Close();
            writer.Close();
            writer.Close();

            // Waiting for durability.
            writer.Line();
            writer.Summary("Waits until a committed sequence is durable. Writers answer their callers only after this completes.");
            writer.Line("/// <param name=\"sequence\">The sequence a commit returned.</param>");
            writer.Line("/// <param name=\"cancellationToken\">Stops waiting (the commit stands).</param>");
            writer.Line("/// <returns>A task that completes when the sequence is durable, or fails if the partition faults first.</returns>");
            writer.Line($"public {Task} WaitUntilDurableAsync(long sequence, global::System.Threading.CancellationToken cancellationToken = default)");
            writer.Open();
            writer.Line($"if (sequence <= {Volatile}.Read(ref this.durableSequence))");
            writer.Open();
            writer.Line($"return {Task}.CompletedTask;");
            writer.Close();
            writer.Line();
            writer.Line($"var waiter = new {Completion}(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);");
            writer.Line("lock (this.durabilityGate)");
            writer.Open();
            writer.Line("if (this.durabilityFailure != null)");
            writer.Open();
            writer.Line($"return {Task}.FromException(DurabilityFailed(this.durabilityFailure));");
            writer.Close();
            writer.Line();
            writer.Line("if (sequence <= this.durableSequence)");
            writer.Open();
            writer.Line($"return {Task}.CompletedTask;");
            writer.Close();
            writer.Line();
            writer.Line("this.durabilityWaiters.Add((sequence, waiter));");
            writer.Close();
            writer.Line();
            writer.Line("return waiter.Task.WaitAsync(cancellationToken);");
            writer.Close();

            // The flusher's side.
            writer.Line();
            writer.Summary("Waits until committed changes are waiting to be flushed. Used by the flusher.");
            writer.Line("/// <param name=\"cancellationToken\">Stops waiting.</param>");
            writer.Line("/// <returns>A task that completes when there is something to flush.</returns>");
            writer.Line($"public {Task} WaitForChangesAsync(global::System.Threading.CancellationToken cancellationToken = default)");
            writer.Open();
            writer.Line($"{Completion} waiter;");
            writer.Line("lock (this.durabilityGate)");
            writer.Open();
            writer.Line($"waiter = this.changeWaiter ??= new {Completion}(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);");
            writer.Close();
            writer.Line();
            writer.Line("if (this.PendingChangeCount > 0)");
            writer.Open();
            writer.Line("this.SignalChanges();");
            writer.Close();
            writer.Line();
            writer.Line("return waiter.Task.WaitAsync(cancellationToken);");
            writer.Close();

            writer.Line();
            writer.Summary("Takes a cut: copies every changed row as a data transfer object and clears the changes. Taken under the lock, so a cut always falls between transactions. Used by the flusher.");
            writer.Line("/// <returns>The cut; write it in one database transaction with its sequence as the watermark, then call MarkDurable.</returns>");
            writer.Line($"public {cut} TakeCut()");
            writer.Open();
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("this.ThrowIfFaulted();");
            writer.Line("if (this.openTransaction != 0)");
            writer.Open();
            writer.Line($"throw new {InvalidOperation}(\"Take a cut between transactions, not inside one.\");");
            writer.Close();
            writer.Line();
            foreach (var table in durable)
            {
                var dto = shape.DtoType(table);
                writer.Line($"var changed{table.Plural} = new global::System.Collections.Generic.List<{dto}>();");
                writer.Line($"var deleted{table.Plural} = new global::System.Collections.Generic.List<{dto}>();");
                writer.Line($"var changed{table.Name}Keys = new global::System.Collections.Generic.HashSet<{shape.KeyType(table)}>();");
            }

            writer.Line();
            writer.Line("foreach (var pair in this.dirtyRows)");
            writer.Open();
            writer.Line("if (pair.Value.Change == Deleted)");
            writer.Open();
            writer.Line("continue;");
            writer.Close();
            writer.Line();
            writer.Line("switch (pair.Value.Table)");
            writer.Open();
            foreach (var table in durable)
            {
                var row = shape.RowType(table);
                writer.Line($"case {table.Name}Table:");
                writer.Line($"    var changed{table.Name} = ({row})pair.Key;");
                writer.Line($"    changed{table.Plural}.Add(changed{table.Name}.ToDto());");
                writer.Line($"    changed{table.Name}Keys.Add({shape.KeyValue(table, $"changed{table.Name}")});");
                writer.Line("    break;");
            }

            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("// A deleted key that was re-inserted in the same cut is simply changed: the database row is kept and updated.");
            writer.Line("foreach (var pair in this.dirtyRows)");
            writer.Open();
            writer.Line("if (pair.Value.Change != Deleted)");
            writer.Open();
            writer.Line("continue;");
            writer.Close();
            writer.Line();
            writer.Line("switch (pair.Value.Table)");
            writer.Open();
            foreach (var table in durable)
            {
                var row = shape.RowType(table);
                writer.Line($"case {table.Name}Table:");
                writer.Line($"    var deleted{table.Name} = ({row})pair.Key;");
                writer.Line($"    if (!changed{table.Name}Keys.Contains({shape.KeyValue(table, $"deleted{table.Name}")}))");
                writer.Line("    {");
                writer.Line($"        deleted{table.Plural}.Add(deleted{table.Name}.ToDto());");
                writer.Line("    }");
                writer.Line();
                writer.Line("    break;");
            }

            writer.Close();
            writer.Close();
            writer.Line();
            foreach (var table in durable)
            {
                writer.Line($"changed{table.Plural}.Sort(static (left, right) => left.RowVersion.CompareTo(right.RowVersion));");
            }

            writer.Line("this.dirtyRows.Clear();");
            // The cut's constructor takes its lists in dependency order, the order a writer applies them.
            var cutOrder = shape.TablesInDependencyOrder().Where(ModelShape.IsDurable);
            var arguments = string.Join(", ", cutOrder.SelectMany(table => new[] { $"changed{table.Plural}", $"deleted{table.Plural}" }));
            writer.Line($"return new {cut}(this.TenantId, this.committedSequence{(arguments.Length > 0 ? ", " + arguments : string.Empty)});");
            writer.Close();
            writer.Close();

            writer.Line();
            writer.Summary("Marks a cut durable: advances the watermark and releases every writer waiting on it. Used by the flusher after its database transaction commits.");
            writer.Line("/// <param name=\"sequence\">The cut's sequence.</param>");
            writer.Line("public void MarkDurable(long sequence)");
            writer.Open();
            writer.Line($"global::System.Collections.Generic.List<{Completion}>? released = null;");
            writer.Line("lock (this.durabilityGate)");
            writer.Open();
            writer.Line("if (sequence <= this.durableSequence)");
            writer.Open();
            writer.Line("return;");
            writer.Close();
            writer.Line();
            writer.Line($"if (sequence > {Volatile}.Read(ref this.committedSequence))");
            writer.Open();
            writer.Line("throw new global::System.ArgumentOutOfRangeException(nameof(sequence), \"A sequence cannot be durable before it is committed.\");");
            writer.Close();
            writer.Line();
            writer.Line($"{Volatile}.Write(ref this.durableSequence, sequence);");
            writer.Line("for (var index = this.durabilityWaiters.Count - 1; index >= 0; index--)");
            writer.Open();
            writer.Line("if (this.durabilityWaiters[index].Sequence <= sequence)");
            writer.Open();
            writer.Line($"(released ??= new global::System.Collections.Generic.List<{Completion}>()).Add(this.durabilityWaiters[index].Waiter);");
            writer.Line("this.durabilityWaiters.RemoveAt(index);");
            writer.Close();
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("if (released != null)");
            writer.Open();
            writer.Line("foreach (var waiter in released)");
            writer.Open();
            writer.Line("waiter.TrySetResult();");
            writer.Close();
            writer.Close();
            writer.Close();

            writer.Line();
            writer.Summary("Stops the partition because its changes cannot be made durable: refuses further transactions and fails every waiting writer. Discard the partition and reload the tenant. Used by the flusher.");
            writer.Line("/// <param name=\"reason\">Why the changes could not be persisted.</param>");
            writer.Line("public void Fault(global::System.Exception reason)");
            writer.Open();
            writer.Line("global::System.ArgumentNullException.ThrowIfNull(reason);");
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("this.isFaulted = true;");
            writer.Close();
            writer.Line();
            writer.Line($"var failed = new global::System.Collections.Generic.List<{Completion}>();");
            writer.Line("lock (this.durabilityGate)");
            writer.Open();
            writer.Line("this.durabilityFailure = reason;");
            writer.Line("foreach (var waiter in this.durabilityWaiters)");
            writer.Open();
            writer.Line("failed.Add(waiter.Waiter);");
            writer.Close();
            writer.Line();
            writer.Line("this.durabilityWaiters.Clear();");
            writer.Close();
            writer.Line();
            writer.Line("foreach (var waiter in failed)");
            writer.Open();
            writer.Line("waiter.TrySetException(DurabilityFailed(reason));");
            writer.Close();
            writer.Line();
            writer.Line("// Wake readers waiting on a commit; their next read finds the fault.");
            writer.Line("this.SignalCommit();");
            writer.Close();

            // Recovery.
            writer.Line();
            writer.Summary("Begins loading a tenant from the database into this empty partition. Load parents first (see TablesInDependencyOrder), then call CompleteLoad.");
            writer.Line("public void BeginLoad()");
            writer.Open();
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("this.ThrowIfFaulted();");
            var counts = durable.Count == 0 ? "false" : string.Join(" || ", durable.Select(table => $"this.{shape.TableField(table)}.Count > 0"));
            writer.Line($"if (this.openTransaction != 0 || this.isLoading || {counts})");
            writer.Open();
            writer.Line($"throw new {InvalidOperation}(\"Load only into an empty partition, outside a transaction.\");");
            writer.Close();
            writer.Line();
            writer.Line("this.isLoading = true;");
            writer.Close();
            writer.Close();

            foreach (var table in durable)
            {
                EmitLoad(shape, table, writer);
            }

            writer.Line();
            writer.Summary("Loads a row of any durable table from its data transfer object, for loaders that hold rows without knowing their tables. Load parents first.");
            writer.Line("/// <param name=\"dto\">The row's data transfer object.</param>");
            writer.Line("/// <exception cref=\"System.ArgumentException\">The object is not a durable table's data transfer object.</exception>");
            writer.Line("public void LoadRow(object dto)");
            writer.Open();
            writer.Line("switch (dto)");
            writer.Open();
            foreach (var table in durable)
            {
                writer.Line($"case {shape.DtoType(table)} {Names.Parameter(table.Name)}:");
                writer.Line($"    this.Load{table.Name}({Names.Parameter(table.Name)});");
                writer.Line("    return;");
            }

            writer.Line("default:");
            writer.Line("    throw new global::System.ArgumentException($\"{dto?.GetType().Name ?? \"null\"} is not a durable table's data transfer object.\", nameof(dto));");
            writer.Close();
            writer.Close();

            writer.Line();
            writer.Summary("Completes a load: the partition resumes after the tenant's durable watermark, and everything loaded counts as durable.");
            writer.Line("/// <param name=\"durableSequence\">The tenant's durable watermark, read from the database with its rows.</param>");
            writer.Line("public void CompleteLoad(long durableSequence)");
            writer.Open();
            writer.Line("long resumed;");
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("this.RequireLoading();");
            writer.Line("this.isLoading = false;");
            writer.Line("if (durableSequence > this.sequence)");
            writer.Open();
            writer.Line("this.sequence = durableSequence;");
            writer.Close();
            writer.Line();
            writer.Line("resumed = this.sequence;");
            writer.Line($"{Volatile}.Write(ref this.committedSequence, resumed);");
            writer.Line("this.journalHorizon = resumed;");
            writer.Close();
            writer.Line();
            writer.Line("lock (this.durabilityGate)");
            writer.Open();
            writer.Line($"{Volatile}.Write(ref this.durableSequence, resumed);");
            writer.Close();
            writer.Close();

            EmitChangeFeed(shape, writer);

            // Private helpers.
            writer.Line();
            writer.Line("private static global::System.Exception DurabilityFailed(global::System.Exception reason)");
            writer.Open();
            writer.Line($"return new {InvalidOperation}(\"The tenant's changes could not be made durable; the partition has stopped and must be reloaded.\", reason);");
            writer.Close();
            writer.Line();
            writer.Line("private void RequireLoading()");
            writer.Open();
            writer.Line("if (!this.isLoading)");
            writer.Open();
            writer.Line($"throw new {InvalidOperation}(\"Call BeginLoad first.\");");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("private void SignalChanges()");
            writer.Open();
            writer.Line($"{Completion}? waiter;");
            writer.Line("lock (this.durabilityGate)");
            writer.Open();
            writer.Line("waiter = this.changeWaiter;");
            writer.Line("this.changeWaiter = null;");
            writer.Close();
            writer.Line();
            writer.Line("waiter?.TrySetResult();");
            writer.Close();
            writer.Line();
            writer.Summary("Records one committed change, coalescing it with any earlier change to the same row since the last cut.");
            writer.Line("private void Track(UndoEntry entry)");
            writer.Open();
            writer.Line("var isTracked = this.dirtyRows.TryGetValue(entry.Row, out var current);");
            writer.Line("if (entry.Change == Deleted)");
            writer.Open();
            writer.Line("if (isTracked && current.Change == Inserted)");
            writer.Open();
            writer.Line("// Inserted and deleted between flushes: the database never sees it.");
            writer.Line("this.dirtyRows.Remove(entry.Row);");
            writer.Close();
            writer.Line("else");
            writer.Open();
            writer.Line("this.dirtyRows[entry.Row] = new DirtyRow(entry.Table, Deleted);");
            writer.Close();
            writer.Close();
            writer.Line("else if (entry.Change == Inserted)");
            writer.Open();
            writer.Line("// A stored row deleted and inserted again is, to the database, changed.");
            writer.Line("this.dirtyRows[entry.Row] = new DirtyRow(entry.Table, isTracked && current.Change == Deleted ? Updated : Inserted);");
            writer.Close();
            writer.Line("else if (!isTracked)");
            writer.Open();
            writer.Line("this.dirtyRows[entry.Row] = new DirtyRow(entry.Table, Updated);");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Summary("A row changed since the last cut, and how.");
            writer.Line("private readonly struct DirtyRow");
            writer.Open();
            writer.Line("public DirtyRow(int table, byte change)");
            writer.Open();
            writer.Line("this.Table = table;");
            writer.Line("this.Change = change;");
            writer.Close();
            writer.Line();
            writer.Line("public int Table { get; }");
            writer.Line();
            writer.Line("public byte Change { get; }");
            writer.Close();
        }

        /// <summary>
        /// Emits the change feed: the journal of committed changes, reading changes since a sequence, snapshots, and waiting
        /// for a commit.  A rules engine and, later, client sync read the tenant through it.
        /// </summary>
        private static void EmitChangeFeed(ModelShape shape, CodeWriter writer)
        {
            var changes = $"{shape.Name}Changes";
            var ordered = shape.TablesInDependencyOrder().Where(ModelShape.IsDurable).ToList();

            writer.Line();
            writer.Summary("Gets the journal's horizon: changes at or before this sequence may no longer be journaled, so a reader that has applied less must take a snapshot.");
            writer.Line("public long JournalHorizon");
            writer.Open();
            writer.Line("get");
            writer.Open();
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("return this.journalHorizon;");
            writer.Close();
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Summary("Gets the number of entries in the journal, for monitoring: about one per row changed since the horizon, plus deletions.");
            writer.Line("public int JournalCount");
            writer.Open();
            writer.Line("get");
            writer.Open();
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("return this.journal.Count - this.supersededJournalEntries;");
            writer.Close();
            writer.Close();
            writer.Close();

            // Waiting for a commit.
            writer.Line();
            writer.Summary("Waits until a transaction commits past a sequence: how a rules engine learns there is something new to read.");
            writer.Line("/// <param name=\"sequence\">The last sequence the reader has applied.</param>");
            writer.Line("/// <param name=\"cancellationToken\">Stops waiting.</param>");
            writer.Line("/// <returns>A task that completes when the committed sequence passes it, or when the partition faults.</returns>");
            writer.Line($"public {Task} WaitForCommitAsync(long sequence, global::System.Threading.CancellationToken cancellationToken = default)");
            writer.Open();
            writer.Line("// Every reader shares one waiter per commit, so waiting never accumulates state.");
            writer.Line($"{Completion} waiter;");
            writer.Line("lock (this.durabilityGate)");
            writer.Open();
            writer.Line($"waiter = this.commitWaiter ??= new {Completion}(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);");
            writer.Close();
            writer.Line();
            writer.Line($"if ({Volatile}.Read(ref this.committedSequence) > sequence || {Volatile}.Read(ref this.isFaulted))");
            writer.Open();
            writer.Line($"return {Task}.CompletedTask;");
            writer.Close();
            writer.Line();
            writer.Line("return waiter.Task.WaitAsync(cancellationToken);");
            writer.Close();

            // Reading changes.
            writer.Line();
            writer.Summary("Reads every committed change after a sequence, coalesced: each changed row once, in its latest state, and each deleted key once. Rows whose latest change is past the limit are left for a later read.");
            writer.Line("/// <param name=\"sequence\">The last sequence the reader has applied.</param>");
            writer.Line("/// <param name=\"throughSequence\">The newest change to include; for example, the durable watermark for client sync. Defaults to everything committed.</param>");
            writer.Line("/// <returns>The changes; if the reader is behind the journal's horizon, they are incomplete and the reader must take a snapshot.</returns>");
            writer.Line($"public {changes} ReadChangesSince(long sequence, long throughSequence = long.MaxValue)");
            writer.Open();
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("this.RequireBetweenTransactions(\"Read changes\");");
            writer.Line("var through = global::System.Math.Min(throughSequence, this.committedSequence);");
            writer.Line("if (sequence < this.journalHorizon)");
            writer.Open();
            writer.Line($"return {changes}.Incomplete(this.TenantId, sequence);");
            writer.Close();
            writer.Line();
            writer.Line("if (through <= sequence)");
            writer.Open();
            writer.Line($"return {changes}.Empty(this.TenantId, sequence);");
            writer.Close();
            writer.Line();
            foreach (var table in ordered)
            {
                var dto = shape.DtoType(table);
                writer.Line($"var changed{table.Plural} = new global::System.Collections.Generic.List<{dto}>();");
                writer.Line($"var deleted{table.Plural} = new global::System.Collections.Generic.List<{dto}>();");
                writer.Line($"var changed{table.Name}Keys = new global::System.Collections.Generic.HashSet<{shape.KeyType(table)}>();");
                writer.Line($"var deleted{table.Name}Keys = new global::System.Collections.Generic.HashSet<{shape.KeyType(table)}>();");
            }

            writer.Line("var deletions = new global::System.Collections.Generic.List<JournalEntry>();");
            writer.Line("for (var index = this.FirstJournalIndexAfter(sequence); index < this.journal.Count; index++)");
            writer.Open();
            writer.Line("var entry = this.journal[index];");
            writer.Line("if (entry.Sequence > through)");
            writer.Open();
            writer.Line("break;");
            writer.Close();
            writer.Line();
            writer.Line("if (entry.IsSuperseded)");
            writer.Open();
            writer.Line("continue;");
            writer.Close();
            writer.Line();
            writer.Line("if (entry.Change == Deleted)");
            writer.Open();
            writer.Line("deletions.Add(entry);");
            writer.Line("continue;");
            writer.Close();
            writer.Line();
            writer.Line("// The row's latest change: its current state is its state as of that change.");
            writer.Line("switch (entry.Table)");
            writer.Open();
            foreach (var table in ordered)
            {
                var row = shape.RowType(table);
                writer.Line($"case {table.Name}Table:");
                writer.Line($"    var changed{table.Name} = ({row})entry.Row;");
                writer.Line($"    changed{table.Plural}.Add(changed{table.Name}.ToDto());");
                writer.Line($"    changed{table.Name}Keys.Add({shape.KeyValue(table, $"changed{table.Name}")});");
                writer.Line("    break;");
            }

            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("// A deleted key that is in the table again (a new row with the same key) is simply changed.");
            writer.Line("foreach (var entry in deletions)");
            writer.Open();
            writer.Line("switch (entry.Table)");
            writer.Open();
            foreach (var table in ordered)
            {
                var row = shape.RowType(table);
                writer.Line($"case {table.Name}Table:");
                writer.Line($"    var deleted{table.Name} = ({row})entry.Row;");
                writer.Line($"    var deleted{table.Name}Key = {shape.KeyValue(table, $"deleted{table.Name}")};");
                writer.Line($"    if (!changed{table.Name}Keys.Contains(deleted{table.Name}Key) && deleted{table.Name}Keys.Add(deleted{table.Name}Key))");
                writer.Line("    {");
                writer.Line($"        deleted{table.Plural}.Add(deleted{table.Name}.ToDto());");
                writer.Line("    }");
                writer.Line();
                writer.Line("    break;");
            }

            writer.Close();
            writer.Close();
            writer.Line();
            var lists = string.Join(", ", ordered.SelectMany(table => new[] { $"changed{table.Plural}", $"deleted{table.Plural}" }));
            writer.Line($"return new {changes}(this.TenantId, sequence, through, true{(lists.Length > 0 ? ", " + lists : string.Empty)});");
            writer.Close();
            writer.Close();

            // Snapshots.
            writer.Line();
            writer.Summary("Takes a snapshot: every row, as of the last commit. A reader starts from it, then reads changes since its sequence.");
            writer.Line("/// <returns>Every row, as changes from sequence zero through the last commit.</returns>");
            writer.Line($"public {changes} TakeSnapshot()");
            writer.Open();
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("this.RequireBetweenTransactions(\"Take a snapshot\");");
            foreach (var table in ordered)
            {
                var dto = shape.DtoType(table);
                writer.Line($"var all{table.Plural} = new global::System.Collections.Generic.List<{dto}>(this.{shape.TableField(table)}.Count);");
                writer.Line($"foreach (var row in this.{shape.TableField(table)}.Values)");
                writer.Open();
                writer.Line($"all{table.Plural}.Add(row.ToDto());");
                writer.Close();
                writer.Line();
            }

            var snapshotLists = string.Join(", ", ordered.SelectMany(table => new[] { $"all{table.Plural}", $"global::System.Array.Empty<{shape.DtoType(table)}>()" }));
            writer.Line($"return new {changes}(this.TenantId, 0, this.committedSequence, true{(snapshotLists.Length > 0 ? ", " + snapshotLists : string.Empty)});");
            writer.Close();
            writer.Close();

            // Trimming.
            writer.Line();
            writer.Summary("Discards journal entries at or before a sequence. Call it with the oldest sequence any reader still needs; a reader behind it must then take a snapshot.");
            writer.Line("/// <param name=\"throughSequence\">The newest sequence that no reader needs any more.</param>");
            writer.Line("public void TrimJournal(long throughSequence)");
            writer.Open();
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("this.RequireBetweenTransactions(\"Trim the journal\");");
            writer.Line("var through = global::System.Math.Min(throughSequence, this.committedSequence);");
            writer.Line("if (through <= this.journalHorizon)");
            writer.Open();
            writer.Line("return;");
            writer.Close();
            writer.Line();
            writer.Line("this.journal.RemoveRange(0, this.FirstJournalIndexAfter(through));");
            writer.Line("this.journalHorizon = through;");
            writer.Line("this.RebuildJournalPositions();");
            writer.Close();
            writer.Close();

            // Helpers.
            writer.Line();
            writer.Line("private void RequireBetweenTransactions(string what)");
            writer.Open();
            writer.Line("this.ThrowIfFaulted();");
            writer.Line("if (this.openTransaction != 0 || this.isLoading)");
            writer.Open();
            writer.Line($"throw new {InvalidOperation}(what + \" between transactions, not inside one or while loading.\");");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("private int FirstJournalIndexAfter(long sequence)");
            writer.Open();
            writer.Line("// The journal is in sequence order, so a binary search finds the first entry after the sequence.");
            writer.Line("var low = 0;");
            writer.Line("var high = this.journal.Count;");
            writer.Line("while (low < high)");
            writer.Open();
            writer.Line("var middle = low + ((high - low) / 2);");
            writer.Line("if (this.journal[middle].Sequence <= sequence)");
            writer.Open();
            writer.Line("low = middle + 1;");
            writer.Close();
            writer.Line("else");
            writer.Open();
            writer.Line("high = middle;");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("return low;");
            writer.Close();
            writer.Line();
            writer.Summary("Records a committed change. Only a row's latest entry is kept live, so the journal holds about one entry per changed row.");
            writer.Line("private void AppendToJournal(UndoEntry entry)");
            writer.Open();
            writer.Line("if (this.journalPositions.TryGetValue(entry.Row, out var previous))");
            writer.Open();
            writer.Line("this.journal[previous] = this.journal[previous].Supersede();");
            writer.Line("this.supersededJournalEntries++;");
            writer.Close();
            writer.Line();
            writer.Line("this.journalPositions[entry.Row] = this.journal.Count;");
            writer.Line("this.journal.Add(new JournalEntry(entry.Table, entry.Change, entry.Row, entry.Sequence, false));");
            writer.Line("if (this.supersededJournalEntries > 1024 && this.supersededJournalEntries * 2 > this.journal.Count)");
            writer.Open();
            writer.Line("this.journal.RemoveAll(static item => item.IsSuperseded);");
            writer.Line("this.RebuildJournalPositions();");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("private void RebuildJournalPositions()");
            writer.Open();
            writer.Line("this.journalPositions.Clear();");
            writer.Line("this.supersededJournalEntries = 0;");
            writer.Line("for (var index = 0; index < this.journal.Count; index++)");
            writer.Open();
            writer.Line("if (this.journal[index].IsSuperseded)");
            writer.Open();
            writer.Line("this.supersededJournalEntries++;");
            writer.Close();
            writer.Line("else");
            writer.Open();
            writer.Line("this.journalPositions[this.journal[index].Row] = index;");
            writer.Close();
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("private void SignalCommit()");
            writer.Open();
            writer.Line($"{Completion}? waiter;");
            writer.Line("lock (this.durabilityGate)");
            writer.Open();
            writer.Line("waiter = this.commitWaiter;");
            writer.Line("this.commitWaiter = null;");
            writer.Close();
            writer.Line();
            writer.Line("waiter?.TrySetResult();");
            writer.Close();
            writer.Line();
            writer.Summary("One committed change in the journal.");
            writer.Line("private readonly struct JournalEntry");
            writer.Open();
            writer.Line("public JournalEntry(int table, byte change, object row, long sequence, bool isSuperseded)");
            writer.Open();
            writer.Line("this.Table = table;");
            writer.Line("this.Change = change;");
            writer.Line("this.Row = row;");
            writer.Line("this.Sequence = sequence;");
            writer.Line("this.IsSuperseded = isSuperseded;");
            writer.Close();
            writer.Line();
            writer.Line("public int Table { get; }");
            writer.Line();
            writer.Line("public byte Change { get; }");
            writer.Line();
            writer.Line("public object Row { get; }");
            writer.Line();
            writer.Line("public long Sequence { get; }");
            writer.Line();
            writer.Line("public bool IsSuperseded { get; }");
            writer.Line();
            writer.Line("public JournalEntry Supersede() => new JournalEntry(this.Table, this.Change, this.Row, this.Sequence, true);");
            writer.Close();
        }

        /// <summary>
        /// Emits the changes type: what a read of the change feed, or a snapshot, returns.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <returns>The source text.</returns>
        public static string EmitChanges(ModelShape shape)
        {
            var ordered = shape.TablesInDependencyOrder().Where(ModelShape.IsDurable).ToList();
            var changes = $"{shape.Name}Changes";
            var lists = "global::System.Collections.Generic.IReadOnlyList";
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary("A tenant's committed changes after one sequence through another, coalesced: each changed row once, in its latest state, and each deleted key once.");
            writer.Line("/// <remarks>");
            writer.Line("/// Apply changed rows table by table in TablesInDependencyOrder, then deleted rows in reverse order. If IsComplete is false, the");
            writer.Line("/// reader fell behind the journal's horizon and must start again from a snapshot.");
            writer.Line("/// </remarks>");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {changes}");
            writer.Open();
            var parameters = string.Join(", ", ordered.SelectMany(table => new[]
            {
                $"{lists}<{shape.DtoType(table)}> changed{table.Plural}",
                $"{lists}<{shape.DtoType(table)}> deleted{table.Plural}",
            }));
            writer.Line($"internal {changes}(global::System.Guid tenantId, long fromSequence, long throughSequence, bool isComplete{(parameters.Length > 0 ? ", " + parameters : string.Empty)})");
            writer.Open();
            writer.Line("this.TenantId = tenantId;");
            writer.Line("this.FromSequence = fromSequence;");
            writer.Line("this.ThroughSequence = throughSequence;");
            writer.Line("this.IsComplete = isComplete;");
            foreach (var table in ordered)
            {
                writer.Line($"this.Changed{table.Plural} = changed{table.Plural};");
                writer.Line($"this.Deleted{table.Plural} = deleted{table.Plural};");
            }

            writer.Close();
            writer.Line();
            writer.Summary("Gets the tenant.");
            writer.Line("public global::System.Guid TenantId { get; }");
            writer.Line();
            writer.Summary("Gets the sequence the changes start after: the reader's sequence, or zero for a snapshot.");
            writer.Line("public long FromSequence { get; }");
            writer.Line();
            writer.Summary("Gets the sequence the changes run through: the reader's new sequence once it applies them.");
            writer.Line("public long ThroughSequence { get; }");
            writer.Line();
            writer.Summary("Gets a value indicating whether the changes are complete; if not, the reader must take a snapshot instead.");
            writer.Line("public bool IsComplete { get; }");
            writer.Line();
            writer.Summary("Gets a value indicating whether there are no changes.");
            var empties = ordered.Count == 0 ? "true" : string.Join(" && ", ordered.SelectMany(table => new[] { $"this.Changed{table.Plural}.Count == 0", $"this.Deleted{table.Plural}.Count == 0" }));
            writer.Line($"public bool IsEmpty => {empties};");
            foreach (var table in ordered)
            {
                writer.Line();
                writer.Summary($"Gets the {table.Plural} inserted or changed, in sequence order, each in its latest state.");
                writer.Line($"public {lists}<{shape.DtoType(table)}> Changed{table.Plural} {{ get; }}");
                writer.Line();
                writer.Summary($"Gets the {table.Plural} deleted, as they were when deleted.");
                writer.Line($"public {lists}<{shape.DtoType(table)}> Deleted{table.Plural} {{ get; }}");
            }

            var factTables = ordered.Where(table => table.HasFacts).ToList();
            writer.Line();
            writer.Summary("Converts the changes to facts for a rules engine, in the order to apply them: changed rows parents first, then retractions children first. Tables marked \"facts\": false are left out.");
            writer.Line("/// <returns>Each fact, and whether it is to be retracted.</returns>");
            writer.Line("public global::System.Collections.Generic.IEnumerable<(object Fact, bool IsRetraction)> ToFacts()");
            writer.Open();
            foreach (var table in factTables)
            {
                writer.Line($"foreach (var dto in this.Changed{table.Plural})");
                writer.Open();
                writer.Line($"yield return (new {table.Name}Fact(dto), false);");
                writer.Close();
                writer.Line();
            }

            for (var index = factTables.Count - 1; index >= 0; index--)
            {
                var table = factTables[index];
                writer.Line($"foreach (var dto in this.Deleted{table.Plural})");
                writer.Open();
                writer.Line($"yield return (new {table.Name}Fact(dto), true);");
                writer.Close();
                writer.Line();
            }

            writer.Line("yield break;");
            writer.Close();
            writer.Line();
            var empty = string.Join(", ", ordered.SelectMany(table => new[] { $"global::System.Array.Empty<{shape.DtoType(table)}>()", $"global::System.Array.Empty<{shape.DtoType(table)}>()" }));
            writer.Line($"internal static {changes} Empty(global::System.Guid tenantId, long sequence) => new {changes}(tenantId, sequence, sequence, true{(empty.Length > 0 ? ", " + empty : string.Empty)});");
            writer.Line();
            writer.Line($"internal static {changes} Incomplete(global::System.Guid tenantId, long sequence) => new {changes}(tenantId, sequence, sequence, false{(empty.Length > 0 ? ", " + empty : string.Empty)});");
            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        /// <summary>
        /// Emits the cut: a tenant's changed and deleted rows, as data transfer objects, at one sequence.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <returns>The source text.</returns>
        public static string EmitCut(ModelShape shape)
        {
            var durable = shape.TablesInDependencyOrder().Where(ModelShape.IsDurable).ToList();
            var cut = $"{shape.Name}Cut";
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary("A consistent cut of one tenant's changes, for a write-behind flusher: every row changed or deleted since the last cut, as of one sequence.");
            writer.Line("/// <remarks>");
            writer.Line("/// Write it in one database transaction: changed rows table by table in TablesInDependencyOrder (each table's rows are in");
            writer.Line("/// version order), then deleted rows in reverse order, then the watermark (Sequence). Then call the partition's MarkDurable.");
            writer.Line("/// </remarks>");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {cut}");
            writer.Open();
            var parameters = string.Join(", ", durable.SelectMany(table => new[]
            {
                $"global::System.Collections.Generic.IReadOnlyList<{shape.DtoType(table)}> changed{table.Plural}",
                $"global::System.Collections.Generic.IReadOnlyList<{shape.DtoType(table)}> deleted{table.Plural}",
            }));
            writer.Line($"internal {cut}(global::System.Guid tenantId, long sequence{(parameters.Length > 0 ? ", " + parameters : string.Empty)})");
            writer.Open();
            writer.Line("this.TenantId = tenantId;");
            writer.Line("this.Sequence = sequence;");
            foreach (var table in durable)
            {
                writer.Line($"this.Changed{table.Plural} = changed{table.Plural};");
                writer.Line($"this.Deleted{table.Plural} = deleted{table.Plural};");
            }

            writer.Close();
            writer.Line();
            writer.Summary("Gets the tenant.");
            writer.Line("public global::System.Guid TenantId { get; }");
            writer.Line();
            writer.Summary("Gets the sequence the cut is consistent at: the watermark to write with it.");
            writer.Line("public long Sequence { get; }");
            writer.Line();
            writer.Summary("Gets a value indicating whether the cut holds no rows.");
            var empties = durable.Count == 0 ? "true" : string.Join(" && ", durable.SelectMany(table => new[] { $"this.Changed{table.Plural}.Count == 0", $"this.Deleted{table.Plural}.Count == 0" }));
            writer.Line($"public bool IsEmpty => {empties};");
            foreach (var table in durable)
            {
                var dto = shape.DtoType(table);
                writer.Line();
                writer.Summary($"Gets the {table.Plural} inserted or changed, in version order, each in its latest state.");
                writer.Line($"public global::System.Collections.Generic.IReadOnlyList<{dto}> Changed{table.Plural} {{ get; }}");
                writer.Line();
                writer.Summary($"Gets the {table.Plural} deleted, as they were when deleted.");
                writer.Line($"public global::System.Collections.Generic.IReadOnlyList<{dto}> Deleted{table.Plural} {{ get; }}");
            }

            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        /// <summary>
        /// Emits the load of one table's row during recovery: checked like an insert, but keeping the stored version, with no
        /// undo entry and no change tracking.
        /// </summary>
        private static void EmitLoad(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            var field = shape.TableField(table);
            writer.Line();
            writer.Summary($"Loads a {table.Name} from the database during recovery, keeping its stored version. Load its parents first.");
            writer.Line($"/// <exception cref=\"System.Data.ConstraintException\">The row is loaded twice, a unique value repeats, or a parent was not loaded first.</exception>");
            writer.Line($"public {row} Load{table.Name}({shape.DtoType(table)} dto)");
            writer.Open();
            writer.Line("lock (this.syncRoot)");
            writer.Open();
            writer.Line("this.RequireLoading();");
            PartitionEmitter.NullCheck(writer, "dto");
            writer.Line($"var row = {row}.FromDto(dto);");
            writer.Line($"var key = {shape.KeyValue(table, "row")};");
            writer.Line($"if (this.{field}.ContainsKey(key))");
            writer.Open();
            writer.Line($"throw new {Constraint}($\"{table.Name} {{key}} is loaded twice.\");");
            writer.Close();
            writer.Line();
            PartitionEmitter.CheckUniquesFree(shape, table, writer, "row", "row", insert: false);
            var parents = PartitionEmitter.ResolveParents(shape, table, writer, "row", movableOnly: false);
            writer.Line("row.TenantId = this.TenantId;");
            writer.Line($"this.{field}.Add(key, row);");
            PartitionEmitter.AddUniques(shape, table, writer, "row");
            foreach (var (reference, variable) in parents)
            {
                writer.Line($"Link{shape.HelperName(table, reference)}(row, {variable});");
            }

            writer.Line("if (row.RowVersion > this.sequence)");
            writer.Open();
            writer.Line("this.sequence = row.RowVersion;");
            writer.Close();
            writer.Line();
            writer.Line("return row;");
            writer.Close();
            writer.Close();
        }
    }
}
