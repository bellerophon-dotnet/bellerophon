// <copyright file="PersistenceTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System;
    using System.Reflection;
    using Xunit;

    /// <summary>
    /// Tests the persistence hooks: change tracking at commit, cuts, the durable watermark and the writers who wait on it,
    /// fail-stop, recovery by loading, and the identical-insert retry.  Like the transaction tests, each scenario is C#
    /// compiled together with the generated code.
    /// </summary>
    public class PersistenceTests
    {
        private static readonly Lazy<Assembly> Server = new Lazy<Assembly>(() => TransactionTests.Compile(Fixture.Read("bellerophon.json"), "Primary", Scenarios));

        private const string Scenarios = TransactionTests.Common + """
            namespace Scenarios
            {
                using System.Collections.Generic;
                using AtlasLedger.Model;
                using AtlasLedger.Model.Primary;

                public static class Persistence
                {
                    private static Node NewNode(string name) => new Node { NodeId = Guid.NewGuid(), DisplayName = name, FullName = name, NodeStatus = NodeStatus.Active, NodeType = NodeType.Account };

                    private static Node Renamed(Node node, string name) => new Node { NodeId = node.NodeId, DisplayName = name, FullName = name, NodeStatus = NodeStatus.Active, NodeType = NodeType.Account, RowVersion = node.RowVersion };

                    private static LedgerPartition WriteBehind() => new Ledger { UsesWriteBehind = true }.GetPartition(Guid.NewGuid());

                    public static void WithoutWriteBehindEveryCommitIsDurable()
                    {
                        var partition = new Ledger().GetPartition(Guid.NewGuid());
                        partition.InsertNode(NewNode("A"));
                        Check.That(partition.CommittedSequence > 0 && partition.DurableSequence == partition.CommittedSequence, "the commit is durable at once");
                        Check.That(partition.WaitUntilDurableAsync(partition.CommittedSequence).IsCompleted, "nothing waits");
                        Check.That(partition.PendingChangeCount == 0 && partition.TakeCut().IsEmpty, "nothing is tracked");
                    }

                    public static void CommitsAreTrackedAndCoalesced()
                    {
                        var partition = WriteBehind();
                        Node node;
                        using (var transaction = partition.BeginTransaction())
                        {
                            node = transaction.InsertNode(NewNode("A"));
                            transaction.UpdateNode(Renamed(node, "B"));
                            transaction.UpdateNode(Renamed(node, "C"));
                            transaction.InsertAccount(new Account { AccountId = node.NodeId, AccountType = AccountType.ManagedAccount });
                            transaction.Commit();
                        }

                        long last;
                        using (var transaction = partition.BeginTransaction())
                        {
                            transaction.InsertSymbol(new Symbol { Scheme = "X", Code = "1", NodeId = node.NodeId });
                            transaction.DeleteSymbol("X", "1", partition.FindSymbol("X", "1")!.RowVersion);
                            last = transaction.Commit();
                        }

                        Check.That(partition.DurableSequence == 0 && partition.PendingChangeCount == 2, "two rows wait for the flusher");
                        var cut = partition.TakeCut();
                        Check.That(cut.Sequence == last && last == partition.CommittedSequence, "the cut is consistent at the last commit");
                        Check.That(cut.ChangedNodes.Count == 1 && cut.ChangedNodes[0].DisplayName == "C", "three changes to the node are one write, in its latest state");
                        Check.That(cut.ChangedAccounts.Count == 1, "the account is written");
                        Check.That(cut.ChangedSymbols.Count == 0 && cut.DeletedSymbols.Count == 0, "a symbol inserted and deleted between flushes never reaches the database");
                        Check.That(partition.PendingChangeCount == 0 && partition.TakeCut().IsEmpty, "a cut clears the changes");
                    }

                    public static void DeletesReachTheCutUnlessTheKeyReturns()
                    {
                        var partition = WriteBehind();
                        var node = partition.InsertNode(NewNode("A"));
                        partition.InsertSymbol(new Symbol { Scheme = "X", Code = "1", NodeId = node.NodeId });
                        partition.InsertSymbol(new Symbol { Scheme = "X", Code = "2", NodeId = node.NodeId });
                        var first = partition.TakeCut();
                        partition.MarkDurable(first.Sequence);

                        partition.DeleteSymbol("X", "1", partition.FindSymbol("X", "1")!.RowVersion);
                        using (var transaction = partition.BeginTransaction())
                        {
                            transaction.DeleteSymbol("X", "2", partition.FindSymbol("X", "2")!.RowVersion);
                            transaction.InsertSymbol(new Symbol { Scheme = "X", Code = "2", NodeId = node.NodeId });
                            transaction.Commit();
                        }

                        var cut = partition.TakeCut();
                        Check.That(cut.DeletedSymbols.Count == 1 && cut.DeletedSymbols[0].Code == "1", "a flushed row that is deleted is deleted in the database");
                        Check.That(cut.ChangedSymbols.Count == 1 && cut.ChangedSymbols[0].Code == "2", "a key deleted and inserted again is simply changed");
                    }

                    public static void RolledBackTransactionsLeaveNothingToFlush()
                    {
                        var partition = WriteBehind();
                        using (var transaction = partition.BeginTransaction())
                        {
                            transaction.InsertNode(NewNode("A"));
                        }

                        Check.That(partition.PendingChangeCount == 0 && partition.TakeCut().IsEmpty, "an undone transaction has nothing to persist");
                    }

                    public static void ACommitThatChangesNothingAfterARollbackIsDurableAtOnce()
                    {
                        var partition = WriteBehind();
                        var node = partition.InsertNode(NewNode("A"));
                        var cut = partition.TakeCut();
                        partition.MarkDurable(cut.Sequence);
                        var durable = partition.DurableSequence;

                        // A transaction that changes rows and rolls back (a refused batch, a refused rules decision).
                        using (var transaction = partition.BeginTransaction())
                        {
                            transaction.InsertNode(NewNode("B"));
                            transaction.UpdateNode(Renamed(node, "C"));
                            transaction.DeleteNode(node.NodeId, partition.FindNode(node.NodeId)!.RowVersion);
                        }

                        Check.That(partition.CommittedSequence == durable, "a rollback leaves the committed sequence where it was");

                        // The identical-insert retry, as the generated store makes it: commit, then wait on the committed sequence.
                        var retried = partition.InsertNode(node);
                        Check.That(object.ReferenceEquals(retried, node), "the retry returns the stored row");
                        Check.That(partition.WaitUntilDurableAsync(partition.CommittedSequence).IsCompleted, "a retry that changed nothing does not wait for a flush that will never come");

                        // A delete of a key that does not exist also commits nothing.
                        Check.That(!partition.DeleteNode(Guid.NewGuid(), 1), "nothing to delete");
                        Check.That(partition.WaitUntilDurableAsync(partition.CommittedSequence).IsCompleted, "a delete that found nothing does not wait");

                        // Sequences stay strictly increasing after a rollback.
                        var next = partition.InsertNode(NewNode("D"));
                        Check.That(next.RowVersion == durable + 1, "the next change takes the next sequence");
                    }

                    // A database with primary keys only: one dictionary per table, keyed by the row's key, holding its JSON.
                    private sealed class KeyOnlyDatabase
                    {
                        public Dictionary<string, Dictionary<string, string>> Tables { get; } = new Dictionary<string, Dictionary<string, string>>();

                        // Cuts in which a cleared alert and a new alert on the same (node, rule) meet: a unique constraint on
                        // (NodeId, Rule) in the database would refuse those whenever the insert ran before the delete.
                        public int FlappingCuts { get; private set; }

                        // Writes a cut with its writes in random order: upserts and deletes interleaved, tables mixed.
                        public void Write(LedgerCut cut, Random random)
                        {
                            var writes = new List<Action>();
                            void Add<T>(string table, IReadOnlyList<T> changed, IReadOnlyList<T> deleted, Func<T, string> key)
                            {
                                var rows = this.Tables.TryGetValue(table, out var existing) ? existing : this.Tables[table] = new Dictionary<string, string>();
                                var keys = changed.Select(key).Concat(deleted.Select(key)).ToList();
                                Check.That(keys.Distinct().Count() == keys.Count, $"each {table} key appears at most once in a cut");
                                writes.AddRange(changed.Select(dto => (Action)(() => rows[key(dto)] = System.Text.Json.JsonSerializer.Serialize(dto))));
                                writes.AddRange(deleted.Select(dto => (Action)(() => rows.Remove(key(dto)))));
                            }

                            if (cut.ChangedAlerts.Any(changed => cut.DeletedAlerts.Any(deleted => deleted.NodeId == changed.NodeId && deleted.Rule == changed.Rule)))
                            {
                                this.FlappingCuts++;
                            }

                            Add("Node", cut.ChangedNodes, cut.DeletedNodes, dto => dto.NodeId.ToString());
                            Add("Alert", cut.ChangedAlerts, cut.DeletedAlerts, dto => dto.AlertId.ToString());
                            Add("Symbol", cut.ChangedSymbols, cut.DeletedSymbols, dto => dto.Scheme + "/" + dto.Code);
                            foreach (var write in writes.OrderBy(_ => random.Next()))
                            {
                                write();
                            }
                        }
                    }

                    public static void AnyWriteOrderOfACutGivesTheSameDatabase()
                    {
                        var flapping = 0;
                        for (var seed = 1; seed <= 25; seed++)
                        {
                            var random = new Random(seed);
                            var partition = WriteBehind();
                            var database = new KeyOnlyDatabase();
                            var nodes = Enumerable.Range(0, 4).Select(index => partition.InsertNode(NewNode("N" + index)).NodeId).ToList();
                            for (var step = 0; step < 400; step++)
                            {
                                var node = nodes[random.Next(nodes.Count)];
                                var rule = random.Next(2) == 0 ? "Cap" : "Wash";
                                var code = random.Next(3).ToString();
                                switch (random.Next(5))
                                {
                                    case 0:
                                        // A compliance alert raised again with a new key, on the same (node, rule) a cleared one held: the flapping case.
                                        partition.InsertAlert(new Alert { AlertId = Guid.NewGuid(), NodeId = node, Rule = rule, Message = "step " + step });
                                        break;
                                    case 1:
                                        if (partition.FindAlertBySubject(node, rule) is { } alert)
                                        {
                                            partition.DeleteAlert(alert.AlertId, alert.RowVersion);
                                        }

                                        break;
                                    case 2:
                                        // Symbol keys are few and reused: deleted and inserted again, sometimes under another node.
                                        if (partition.FindSymbol("X", code) is { } symbol)
                                        {
                                            partition.DeleteSymbol("X", code, symbol.RowVersion);
                                        }
                                        else
                                        {
                                            partition.InsertSymbol(new Symbol { Scheme = "X", Code = code, NodeId = node });
                                        }

                                        break;
                                    case 3:
                                        partition.UpdateNode(Renamed(partition.FindNode(node)!, "S" + step));
                                        break;
                                    default:
                                        // A transaction that rolls back: nothing reaches the database.
                                        using (var transaction = partition.BeginTransaction())
                                        {
                                            transaction.InsertSymbol(new Symbol { Scheme = "Y", Code = code, NodeId = node });
                                        }

                                        break;
                                }

                                if (random.Next(15) == 0 || step == 399)
                                {
                                    var cut = partition.TakeCut();
                                    database.Write(cut, random);
                                    partition.MarkDurable(cut.Sequence);
                                }
                            }

                            flapping += database.FlappingCuts;
                            lock (partition.SyncRoot)
                            {
                                var expected = new Dictionary<string, Dictionary<string, string>>
                                {
                                    ["Node"] = partition.Nodes.ToDictionary(row => row.NodeId.ToString(), row => System.Text.Json.JsonSerializer.Serialize(row.ToDto())),
                                    ["Alert"] = partition.Alerts.ToDictionary(row => row.AlertId.ToString(), row => System.Text.Json.JsonSerializer.Serialize(row.ToDto())),
                                    ["Symbol"] = partition.Symbols.ToDictionary(row => row.Scheme + "/" + row.Code, row => System.Text.Json.JsonSerializer.Serialize(row.ToDto())),
                                };
                                foreach (var table in expected.Keys)
                                {
                                    var stored = database.Tables.TryGetValue(table, out var rows) ? rows : new Dictionary<string, string>();
                                    Check.That(stored.Count == expected[table].Count && stored.All(pair => expected[table].TryGetValue(pair.Key, out var json) && json == pair.Value), $"seed {seed}: the database holds exactly the ledger's {table} rows, whatever order each cut was written in");
                                }
                            }
                        }

                        Check.That(flapping > 0, "the runs include cuts where a cleared alert and its replacement share a (node, rule)");
                    }

                    public static void WritersWaitUntilTheirCommitIsDurable()
                    {
                        var partition = WriteBehind();
                        partition.InsertNode(NewNode("A"));
                        var earlier = partition.CommittedSequence;
                        partition.InsertNode(NewNode("B"));
                        var later = partition.CommittedSequence;
                        var waiting = partition.WaitUntilDurableAsync(later);
                        Check.That(!waiting.IsCompleted, "the writer waits for the flusher");
                        partition.MarkDurable(earlier);
                        Check.That(!waiting.Wait(100), "an earlier watermark does not release it");
                        partition.MarkDurable(later);
                        Check.That(waiting.Wait(2000), "its own watermark does");
                        Check.Throws<ArgumentOutOfRangeException>(() => partition.MarkDurable(later + 1), "nothing is durable before it is committed");
                    }

                    public static void TheFlusherWakesWhenThereAreChanges()
                    {
                        var partition = WriteBehind();
                        var waiting = partition.WaitForChangesAsync();
                        Check.That(!waiting.Wait(100), "nothing to flush yet");
                        partition.InsertNode(NewNode("A"));
                        Check.That(waiting.Wait(2000), "a commit wakes the flusher");
                        Check.That(partition.WaitForChangesAsync().IsCompleted, "unflushed changes wake it at once");
                    }

                    public static void FaultStopsThePartitionAndFailsWaiters()
                    {
                        var ledger = new Ledger { UsesWriteBehind = true };
                        var tenant = Guid.NewGuid();
                        var partition = ledger.GetPartition(tenant);
                        partition.InsertNode(NewNode("A"));
                        var waiting = partition.WaitUntilDurableAsync(partition.CommittedSequence);
                        partition.Fault(new Exception("The database is unavailable."));
                        try
                        {
                            waiting.Wait(2000);
                            Check.That(false, "the waiting writer fails");
                        }
                        catch (AggregateException exception) when (exception.InnerException is InvalidOperationException)
                        {
                        }

                        Check.That(partition.IsFaulted, "the partition is faulted");
                        Check.Throws<InvalidOperationException>(() => { var refused = partition.BeginTransaction(); refused.Dispose(); }, "no further transactions");
                        Check.That(ledger.DiscardPartition(tenant) && ledger.GetPartition(tenant) != partition, "the tenant can be discarded and reloaded");
                    }

                    public static void LoadingRebuildsAPartition()
                    {
                        var source = WriteBehind();
                        var node = source.InsertNode(NewNode("A"));
                        source.InsertAccount(new Account { AccountId = node.NodeId, AccountType = AccountType.ManagedAccount });
                        source.InsertSymbol(new Symbol { Scheme = "X", Code = "1", NodeId = node.NodeId });
                        source.InsertUser(new User { UserId = Guid.NewGuid(), IdentityIssuer = "issuer", IdentitySubject = "subject" });
                        var cut = source.TakeCut();

                        var target = new Ledger().GetPartition(source.TenantId);
                        target.BeginLoad();
                        Check.Throws<System.Data.ConstraintException>(() => target.LoadAccount(cut.ChangedAccounts[0]), "a child cannot load before its parent");
                        foreach (var table in Ledger.TablesInDependencyOrder)
                        {
                            switch (table)
                            {
                                case "Node": foreach (var dto in cut.ChangedNodes) { target.LoadNode(dto); } break;
                                case "Account": foreach (var dto in cut.ChangedAccounts) { target.LoadAccount(dto); } break;
                                case "Symbol": foreach (var dto in cut.ChangedSymbols) { target.LoadSymbol(dto); } break;
                                case "User": foreach (var dto in cut.ChangedUsers) { target.LoadUser(dto); } break;
                            }
                        }

                        Check.Throws<InvalidOperationException>(() => { var refused = target.BeginTransaction(); refused.Dispose(); }, "no transactions while loading");
                        target.CompleteLoad(cut.Sequence);
                        var loaded = target.FindNode(node.NodeId)!;
                        Check.That(loaded.Account != null && loaded.Symbols.Count == 1, "links are rebuilt");
                        Check.That(loaded.RowVersion == node.RowVersion, "versions are kept");
                        Check.That(target.FindUserByIdentity("issuer", "subject") != null, "unique indexes are rebuilt");
                        Check.That(target.CommittedSequence == cut.Sequence && target.DurableSequence == cut.Sequence, "the partition resumes at the watermark");
                        Check.That(target.InsertNode(NewNode("B")).RowVersion == cut.Sequence + 1, "new versions continue after it");
                    }

                    public static void ConcurrentFlushingNeverLosesACommittedChange()
                    {
                        var partition = WriteBehind();
                        var database = new Dictionary<Guid, long>();
                        var stop = new CancellationTokenSource();
                        var flusher = new Thread(() =>
                        {
                            while (true)
                            {
                                try
                                {
                                    partition.WaitForChangesAsync(stop.Token).Wait();
                                }
                                catch (AggregateException)
                                {
                                    return;
                                }

                                var cut = partition.TakeCut();
                                lock (database)
                                {
                                    foreach (var dto in cut.ChangedNodes) { database[dto.NodeId] = dto.RowVersion; }
                                    foreach (var dto in cut.DeletedNodes) { database.Remove(dto.NodeId); }
                                }

                                partition.MarkDurable(cut.Sequence);
                            }
                        });
                        flusher.Start();
                        var writers = Enumerable.Range(0, 6).Select(index => new Thread(() =>
                        {
                            var random = new Random(index);
                            var mine = new List<Guid>();
                            for (var iteration = 0; iteration < 150; iteration++)
                            {
                                var before = new List<Guid>(mine);
                                var sequence = 0L;
                                using (var transaction = partition.BeginTransaction())
                                {
                                    if (mine.Count > 0 && random.Next(3) == 0)
                                    {
                                        var id = mine[random.Next(mine.Count)];
                                        var current = partition.FindNode(id)!;
                                        if (random.Next(2) == 0)
                                        {
                                            transaction.UpdateNode(Renamed(current, "U" + iteration));
                                        }
                                        else
                                        {
                                            transaction.DeleteNode(id, current.RowVersion);
                                            mine.Remove(id);
                                        }
                                    }
                                    else
                                    {
                                        mine.Add(transaction.InsertNode(NewNode("N")).NodeId);
                                    }

                                    // One transaction in five is rolled back.
                                    if (random.Next(5) != 0)
                                    {
                                        sequence = transaction.Commit();
                                    }
                                }

                                if (sequence == 0)
                                {
                                    mine = before;
                                    continue;
                                }

                                Check.That(partition.WaitUntilDurableAsync(sequence).Wait(5000), "every commit becomes durable");
                            }
                        })).ToList();
                        writers.ForEach(thread => thread.Start());
                        writers.ForEach(thread => thread.Join());
                        stop.Cancel();
                        flusher.Join();

                        var remaining = partition.TakeCut();
                        lock (database)
                        {
                            foreach (var dto in remaining.ChangedNodes) { database[dto.NodeId] = dto.RowVersion; }
                            foreach (var dto in remaining.DeletedNodes) { database.Remove(dto.NodeId); }
                        }

                        lock (partition.SyncRoot)
                        {
                            Check.That(database.Count == partition.Nodes.Count, $"the database holds {database.Count} nodes and memory {partition.Nodes.Count}");
                            foreach (var node in partition.Nodes)
                            {
                                Check.That(database.TryGetValue(node.NodeId, out var version) && version == node.RowVersion, "every row reached the database in its latest state");
                            }
                        }
                    }

                    public static void TablesAreOrderedParentsFirst()
                    {
                        var order = Ledger.TablesInDependencyOrder.ToList();
                        Check.That(order.IndexOf("Node") < order.IndexOf("Account") && order.IndexOf("Node") < order.IndexOf("Symbol") && order.IndexOf("Node") < order.IndexOf("Edge"), "nodes come before what references them");
                        Check.That(order.IndexOf("Person") < order.IndexOf("Dossier") && order.IndexOf("Person") < order.IndexOf("Member") && order.IndexOf("User") < order.IndexOf("Member"), "people and users come before members");
                    }
                }
            }
            """;

        /// <summary>
        /// Without write-behind, nothing is tracked and every commit is durable at once.
        /// </summary>
        [Fact]
        public void WithoutWriteBehindEveryCommitIsDurable() => TransactionTests.Run(Server, "Persistence", nameof(this.WithoutWriteBehindEveryCommitIsDurable));

        /// <summary>
        /// Commits are tracked for the flusher and coalesced: several changes to a row become one write, and a row inserted
        /// and deleted between flushes never reaches the database.
        /// </summary>
        [Fact]
        public void CommitsAreTrackedAndCoalesced() => TransactionTests.Run(Server, "Persistence", nameof(this.CommitsAreTrackedAndCoalesced));

        /// <summary>
        /// A flushed row that is deleted appears among the cut's deletions, unless its key was inserted again.
        /// </summary>
        [Fact]
        public void DeletesReachTheCutUnlessTheKeyReturns() => TransactionTests.Run(Server, "Persistence", nameof(this.DeletesReachTheCutUnlessTheKeyReturns));

        /// <summary>
        /// A rolled-back transaction leaves nothing for the flusher.
        /// </summary>
        [Fact]
        public void RolledBackTransactionsLeaveNothingToFlush() => TransactionTests.Run(Server, "Persistence", nameof(this.RolledBackTransactionsLeaveNothingToFlush));

        /// <summary>
        /// A rolled-back transaction gives back the sequences it took, so a later commit that changes nothing (an identical
        /// retry, a delete that finds nothing) is durable at once instead of waiting for a flush that never comes.
        /// </summary>
        [Fact]
        public void ACommitThatChangesNothingAfterARollbackIsDurableAtOnce() => TransactionTests.Run(Server, "Persistence", nameof(this.ACommitThatChangesNothingAfterARollbackIsDurableAtOnce));

        /// <summary>
        /// A cut holds each key at most once, so a database with primary keys only (no unique constraints, no foreign keys)
        /// reaches the ledger's state whatever order the cut's writes run in.  Random inserts, updates, and deletes with reused
        /// keys, flapping alerts, and rollbacks; each cut written in a shuffled order.
        /// </summary>
        [Fact]
        public void AnyWriteOrderOfACutGivesTheSameDatabase() => TransactionTests.Run(Server, "Persistence", nameof(this.AnyWriteOrderOfACutGivesTheSameDatabase));

        /// <summary>
        /// A writer waits until the watermark reaches its own commit.
        /// </summary>
        [Fact]
        public void WritersWaitUntilTheirCommitIsDurable() => TransactionTests.Run(Server, "Persistence", nameof(this.WritersWaitUntilTheirCommitIsDurable));

        /// <summary>
        /// The flusher sleeps until there are changes and wakes when a commit makes some.
        /// </summary>
        [Fact]
        public void TheFlusherWakesWhenThereAreChanges() => TransactionTests.Run(Server, "Persistence", nameof(this.TheFlusherWakesWhenThereAreChanges));

        /// <summary>
        /// A fault stops the partition, fails waiting writers, and lets the tenant be discarded and reloaded.
        /// </summary>
        [Fact]
        public void FaultStopsThePartitionAndFailsWaiters() => TransactionTests.Run(Server, "Persistence", nameof(this.FaultStopsThePartitionAndFailsWaiters));

        /// <summary>
        /// Loading rebuilds a partition from its rows, parents first, keeping versions and resuming at the watermark.
        /// </summary>
        [Fact]
        public void LoadingRebuildsAPartition() => TransactionTests.Run(Server, "Persistence", nameof(this.LoadingRebuildsAPartition));

        /// <summary>
        /// Six writers commit, roll back, update, and delete while a flusher takes cuts; every commit becomes durable, and the
        /// flushed copy ends identical to memory.
        /// </summary>
        [Fact]
        public void ConcurrentFlushingNeverLosesACommittedChange() => TransactionTests.Run(Server, "Persistence", nameof(this.ConcurrentFlushingNeverLosesACommittedChange));

        /// <summary>
        /// Tables are ordered parents first, the order a database writer applies changes in.
        /// </summary>
        [Fact]
        public void TablesAreOrderedParentsFirst() => TransactionTests.Run(Server, "Persistence", nameof(this.TablesAreOrderedParentsFirst));
    }
}
