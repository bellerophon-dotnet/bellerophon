// <copyright file="RulesEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Linq;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits the adapter between a model and the rules engine, for a primary-role project that references the rules
    /// library: the rule actions, where rules record their decisions, and the rules tenant, which feeds the engine facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Facts come from two places.  Committed rows come through the partition's change feed, in commit order and coalesced.
    /// Latest values (prices) come from their lock-free stores: each publication only records the key and wakes the
    /// engine, and a cycle reads each changed key once, at its latest value, however often it changed.
    /// </para>
    /// <para>
    /// The rules' decisions are recorded as changes to make, and committed after the rules fire, in one transaction.
    /// Nothing outside the data model runs inside that transaction.
    /// </para>
    /// </remarks>
    internal static class RulesEmitter
    {
        private const string Rules = "global::Bellerophon.Rules";

        private const string Completion = "global::System.Threading.Tasks.TaskCompletionSource";

        /// <summary>
        /// Emits the rule actions: the buffer in which rules record their decisions.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <returns>The source text.</returns>
        public static string EmitActions(ModelShape shape)
        {
            var actions = $"{shape.Name}RuleActions";
            var change = $"global::System.Action<{shape.Name}Transaction>";
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary("Where rules record their decisions during a cycle: changes to make, committed together in one transaction after the rules fire.");
            writer.Line("/// <remarks>");
            writer.Line("/// A rule resolves the tenant's instance as a dependency and records a change such as");
            writer.Line("/// <c>actions.Add(transaction => transaction.InsertAlert(alert))</c>. If any change is refused (a stale version, a broken");
            writer.Line("/// constraint), none is committed, and the next cycle decides again from the newer data.");
            writer.Line("/// </remarks>");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {actions}");
            writer.Open();
            writer.Line("private readonly global::System.Threading.Lock gate = new global::System.Threading.Lock();");
            writer.Line();
            writer.Line($"private global::System.Collections.Generic.List<{change}> pending = new global::System.Collections.Generic.List<{change}>();");
            writer.Line();
            writer.Summary("Gets the number of changes recorded and not yet committed.");
            writer.Line("public int Count");
            writer.Open();
            writer.Line("get");
            writer.Open();
            writer.Line("lock (this.gate)");
            writer.Open();
            writer.Line("return this.pending.Count;");
            writer.Close();
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Summary("Records a change to make in the cycle's transaction. It runs after the rules fire; it must change only the data model.");
            writer.Line("/// <param name=\"change\">The change.</param>");
            writer.Line($"public void Add({change} change)");
            writer.Open();
            writer.Line("global::System.ArgumentNullException.ThrowIfNull(change);");
            writer.Line("lock (this.gate)");
            writer.Open();
            writer.Line("this.pending.Add(change);");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line($"internal global::System.Collections.Generic.List<{change}> Drain()");
            writer.Open();
            writer.Line("lock (this.gate)");
            writer.Open();
            writer.Line("var drained = this.pending;");
            writer.Line($"this.pending = new global::System.Collections.Generic.List<{change}>();");
            writer.Line("return drained;");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("internal void Discard()");
            writer.Open();
            writer.Line("lock (this.gate)");
            writer.Open();
            writer.Line("this.pending.Clear();");
            writer.Close();
            writer.Close();
            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        /// <summary>
        /// Emits the rules tenant: the engine's view of one tenant's partition.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <returns>The source text.</returns>
        public static string EmitTenant(ModelShape shape)
        {
            var tenant = $"{shape.Name}RulesTenant";
            var actions = $"{shape.Name}RuleActions";
            var partition = shape.PartitionName;
            var latest = shape.FactTables.Where(table => table.Concurrency == ConcurrencyMode.LatestValue).ToList();
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary($"One tenant of the {shape.Name} model as the rules engine sees it: facts from the partition's change feed and latest values, and decisions committed through its transactions.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {tenant} : {Rules}.IRulesTenant, global::System.IDisposable");
            writer.Open();
            writer.Line($"private readonly {partition} partition;");
            writer.Line();
            writer.Line("private readonly global::System.Threading.Lock signalGate = new global::System.Threading.Lock();");
            foreach (var table in latest)
            {
                writer.Line();
                writer.Line($"private readonly global::System.Collections.Concurrent.ConcurrentDictionary<{shape.KeyType(table)}, {shape.RowType(table)}> changed{table.Plural} = new global::System.Collections.Concurrent.ConcurrentDictionary<{shape.KeyType(table)}, {shape.RowType(table)}>();");
            }

            writer.Line();
            writer.Line($"private {Completion}? latestValueWaiter;");
            writer.Line();
            writer.Summary($"Initializes a new instance of the {tenant} class. Dispose it (or let the engine host dispose it) to stop listening for latest values.");
            writer.Line("/// <param name=\"partition\">The tenant's partition.</param>");
            writer.Line($"public {tenant}({partition} partition)");
            writer.Open();
            writer.Line("this.partition = partition ?? throw new global::System.ArgumentNullException(nameof(partition));");
            foreach (var table in latest)
            {
                writer.Line($"partition.{table.Name}Published += this.On{table.Name}Changed;");
                writer.Line($"partition.{table.Name}Removed += this.On{table.Name}Changed;");
            }

            writer.Close();
            writer.Line();
            writer.Summary("Gets the tenant.");
            writer.Line("public global::System.Guid TenantId => this.partition.TenantId;");
            writer.Line();
            writer.Summary("Gets the tenant's partition.");
            writer.Line($"public {partition} Partition => this.partition;");
            writer.Line();
            writer.Summary("Gets where the tenant's rules record their decisions.");
            writer.Line($"public {actions} Actions {{ get; }} = new {actions}();");

            // Services for rules.
            writer.Line();
            writer.Summary("Resolves a service a rule depends on: the rule actions, the partition, or this tenant.");
            writer.Line("/// <param name=\"serviceType\">The service's type.</param>");
            writer.Line("/// <returns>The service, or null.</returns>");
            writer.Line("public object? GetService(global::System.Type serviceType)");
            writer.Open();
            writer.Line($"if (serviceType == typeof({actions}))");
            writer.Open();
            writer.Line("return this.Actions;");
            writer.Close();
            writer.Line();
            writer.Line($"if (serviceType == typeof({partition}))");
            writer.Open();
            writer.Line("return this.partition;");
            writer.Close();
            writer.Line();
            writer.Line($"return serviceType == typeof({tenant}) || serviceType == typeof({Rules}.IRulesTenant) ? this : null;");
            writer.Close();

            // Snapshot.
            writer.Line();
            writer.Summary("Takes a snapshot of every fact: every committed row, and every latest value.");
            writer.Line("/// <returns>The facts.</returns>");
            writer.Line($"public {Rules}.FactBatch TakeSnapshot()");
            writer.Open();
            writer.Line("// Forget recorded changes first: any that arrive while the snapshot is read are read again by the next cycle.");
            foreach (var table in latest)
            {
                writer.Line($"this.changed{table.Plural}.Clear();");
            }

            writer.Line("var snapshot = this.partition.TakeSnapshot();");
            writer.Line($"var changes = new global::System.Collections.Generic.List<{Rules}.FactChange>();");
            writer.Line("foreach (var (fact, isRetraction) in snapshot.ToFacts())");
            writer.Open();
            writer.Line($"changes.Add(new {Rules}.FactChange(fact, isRetraction));");
            writer.Close();
            writer.Line();
            foreach (var table in latest)
            {
                writer.Line($"foreach (var row in this.partition.{Names.Identifier(table.Plural)})");
                writer.Open();
                writer.Line($"changes.Add(new {Rules}.FactChange(new {table.Name}Fact(row), false));");
                writer.Close();
                writer.Line();
            }

            writer.Line($"return new {Rules}.FactBatch(snapshot.ThroughSequence, true, changes);");
            writer.Close();

            // Changes.
            writer.Line();
            writer.Summary("Reads the committed changes since a sequence, and every latest value that changed since the last read, once each.");
            writer.Line("/// <param name=\"sequence\">The engine's position.</param>");
            writer.Line("/// <returns>The changes, or an incomplete batch if the engine fell behind the journal.</returns>");
            writer.Line($"public {Rules}.FactBatch ReadChangesSince(long sequence)");
            writer.Open();
            writer.Line("var read = this.partition.ReadChangesSince(sequence);");
            writer.Line("if (!read.IsComplete)");
            writer.Open();
            writer.Line($"return {Rules}.FactBatch.Incomplete(sequence);");
            writer.Close();
            writer.Line();
            writer.Line($"var changes = new global::System.Collections.Generic.List<{Rules}.FactChange>();");
            writer.Line("foreach (var (fact, isRetraction) in read.ToFacts())");
            writer.Open();
            writer.Line($"changes.Add(new {Rules}.FactChange(fact, isRetraction));");
            writer.Close();
            writer.Line();
            foreach (var table in latest)
            {
                var keyArguments = table.Key.Count == 1
                    ? "key"
                    : string.Join(", ", Enumerable.Range(1, table.Key.Count).Select(index => $"key.Item{index}"));
                writer.Line($"foreach (var key in this.changed{table.Plural}.Keys)");
                writer.Open();
                writer.Line($"if (this.changed{table.Plural}.TryRemove(key, out var recorded))");
                writer.Open();
                writer.Line("// Read the latest value now, in case a newer one arrived after it was recorded.");
                writer.Line($"var current = this.partition.Find{table.Name}({keyArguments});");
                writer.Line($"changes.Add(current != null ? new {Rules}.FactChange(new {table.Name}Fact(current), false) : new {Rules}.FactChange(new {table.Name}Fact(recorded), true));");
                writer.Close();
                writer.Close();
                writer.Line();
            }

            writer.Line($"return new {Rules}.FactBatch(read.ThroughSequence, true, changes);");
            writer.Close();

            // Waiting.
            writer.Line();
            writer.Summary("Waits until something is new: a commit past the sequence, or a latest value that changed.");
            writer.Line("/// <param name=\"sequence\">The engine's position.</param>");
            writer.Line("/// <param name=\"cancellationToken\">Stops waiting.</param>");
            writer.Line("/// <returns>A task that completes when there is something to read.</returns>");
            writer.Line("public global::System.Threading.Tasks.Task WaitForChangesAsync(long sequence, global::System.Threading.CancellationToken cancellationToken)");
            writer.Open();
            writer.Line($"{Completion} latestValues;");
            writer.Line("lock (this.signalGate)");
            writer.Open();
            writer.Line($"latestValues = this.latestValueWaiter ??= new {Completion}(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);");
            writer.Close();
            writer.Line();
            var anyLatest = latest.Count == 0 ? "false" : string.Join(" || ", latest.Select(table => $"!this.changed{table.Plural}.IsEmpty"));
            writer.Line($"if ({anyLatest})");
            writer.Open();
            writer.Line("return global::System.Threading.Tasks.Task.CompletedTask;");
            writer.Close();
            writer.Line();
            writer.Line("var commit = this.partition.WaitForCommitAsync(sequence);");
            writer.Line("return commit.IsCompleted ? commit : WaitForEitherAsync(commit, latestValues.Task, cancellationToken);");
            writer.Close();

            // Decisions.
            writer.Line();
            writer.Summary("Commits the decisions the rules recorded, all in one transaction.");
            writer.Line("/// <returns>The transaction's sequence, or zero if there was nothing to commit.</returns>");
            writer.Line("public long ApplyActions()");
            writer.Open();
            writer.Line("var pending = this.Actions.Drain();");
            writer.Line("if (pending.Count == 0)");
            writer.Open();
            writer.Line("return 0;");
            writer.Close();
            writer.Line();
            writer.Line("using var transaction = this.partition.BeginTransaction();");
            writer.Line("foreach (var change in pending)");
            writer.Open();
            writer.Line("change(transaction);");
            writer.Close();
            writer.Line();
            writer.Line("return transaction.Commit();");
            writer.Close();
            writer.Line();
            writer.Summary("Discards decisions recorded by a cycle that failed.");
            writer.Line("public void DiscardActions() => this.Actions.Discard();");
            writer.Line();
            writer.Summary("Stops listening for latest values.");
            writer.Line("public void Dispose()");
            writer.Open();
            foreach (var table in latest)
            {
                writer.Line($"this.partition.{table.Name}Published -= this.On{table.Name}Changed;");
                writer.Line($"this.partition.{table.Name}Removed -= this.On{table.Name}Changed;");
            }

            writer.Close();

            writer.Line();
            writer.Line("private static async global::System.Threading.Tasks.Task WaitForEitherAsync(global::System.Threading.Tasks.Task first, global::System.Threading.Tasks.Task second, global::System.Threading.CancellationToken cancellationToken)");
            writer.Open();
            writer.Line("// The two waits are shared by every caller and complete at the next commit or value; only this call's cancellation is its own.");
            writer.Line($"var cancelled = new {Completion}(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);");
            writer.Line("using (cancellationToken.Register(static state => (({Completion})state!).TrySetCanceled(), cancelled))".Replace("{Completion}", Completion));
            writer.Open();
            writer.Line("await (await global::System.Threading.Tasks.Task.WhenAny(first, second, cancelled.Task).ConfigureAwait(false)).ConfigureAwait(false);");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("private void SignalLatestValues()");
            writer.Open();
            writer.Line($"{Completion}? waiter;");
            writer.Line("lock (this.signalGate)");
            writer.Open();
            writer.Line("waiter = this.latestValueWaiter;");
            writer.Line("this.latestValueWaiter = null;");
            writer.Close();
            writer.Line();
            writer.Line("waiter?.TrySetResult();");
            writer.Close();
            foreach (var table in latest)
            {
                writer.Line();
                writer.Line($"private void On{table.Name}Changed(object? sender, {shape.RowType(table)} row)");
                writer.Open();
                writer.Line("// Only record the key and wake the engine: the cycle reads the latest value, so a burst becomes one change.");
                writer.Line($"this.changed{table.Plural}[{shape.KeyValue(table, "row")}] = row;");
                writer.Line("this.SignalLatestValues();");
                writer.Close();
            }

            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
