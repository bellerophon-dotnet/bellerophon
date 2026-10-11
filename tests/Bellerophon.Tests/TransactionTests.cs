// <copyright file="TransactionTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using Microsoft.CodeAnalysis;
    using Xunit;

    /// <summary>
    /// A deep test of transactions.  A transaction is a <c>ref struct</c>, which reflection and <c>dynamic</c> cannot reach, so
    /// each scenario is C# compiled together with the generated code, exactly as a user would write it; the test runs it.
    /// </summary>
    public class TransactionTests
    {
        private static readonly Lazy<Assembly> Server = new Lazy<Assembly>(() => Compile(Fixture.Read("bellerophon.json"), "Primary", ServerScenarios));

        private static readonly Lazy<Assembly> Client = new Lazy<Assembly>(() => Compile(Fixture.Read("bellerophon.json"), "Replica", ClientScenarios));

        private static readonly Lazy<Assembly> Market = new Lazy<Assembly>(() => Compile(MarketModel, "Primary", MarketScenarios, includeEnums: false));

        internal const string Common = """
            using System;
            using System.Data;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;

            public static class Check
            {
                public static void That(bool condition, string what)
                {
                    if (!condition)
                    {
                        throw new Exception("Failed: " + what);
                    }
                }

                public static void Throws<T>(Action action, string what) where T : Exception
                {
                    try
                    {
                        action();
                    }
                    catch (T)
                    {
                        return;
                    }

                    throw new Exception("Expected " + typeof(T).Name + ": " + what);
                }
            }
            """;

        private const string ServerScenarios = Common + """
            namespace Scenarios
            {
                using AtlasLedger.Model;
                using AtlasLedger.Model.Primary;

                public static class Server
                {
                    private static Node NewNode(string name) => new Node { NodeId = Guid.NewGuid(), DisplayName = name, FullName = name, NodeStatus = NodeStatus.Active, NodeType = NodeType.Account };

                    private static LedgerPartition NewPartition() => new Ledger().GetPartition(Guid.NewGuid());

                    // The sets are private, so the test looks at them through reflection.
                    private static object? SetOf(Node node, string field) =>
                        typeof(Node).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(node);

                    public static void CommitKeepsEveryChange()
                    {
                        var partition = NewPartition();
                        using (var transaction = partition.BeginTransaction())
                        {
                            var node = transaction.InsertNode(NewNode("A"));
                            transaction.InsertAccount(new Account { AccountId = node.NodeId, AccountType = AccountType.ManagedAccount });
                            transaction.InsertSymbol(new Symbol { Scheme = "Custodian", Code = "1", NodeId = node.NodeId });
                            transaction.Commit();
                        }

                        var stored = partition.Nodes.Single();
                        Check.That(partition.Accounts.Count == 1 && partition.Symbols.Count == 1, "every row was kept");
                        Check.That(stored.Account != null && stored.Symbols.Count == 1, "every link was kept");
                    }

                    public static void DisposeWithoutCommitUndoesEverything()
                    {
                        var partition = NewPartition();
                        var a = partition.InsertNode(NewNode("A"));
                        var symbol = partition.InsertSymbol(new Symbol { Scheme = "Custodian", Code = "1", NodeId = a.NodeId });
                        var user = partition.InsertUser(new User { UserId = Guid.NewGuid(), IdentityIssuer = "issuer", IdentitySubject = "s1" });
                        var symbolVersion = symbol.RowVersion;
                        var userVersion = user.RowVersion;

                        using (var transaction = partition.BeginTransaction())
                        {
                            var b = transaction.InsertNode(NewNode("B"));
                            transaction.InsertAccount(new Account { AccountId = b.NodeId, AccountType = AccountType.GroupAccount });
                            var moved = transaction.UpdateSymbol(new Symbol { Scheme = "Custodian", Code = "1", NodeId = b.NodeId, RowVersion = symbolVersion })!;
                            transaction.InsertUser(new User { UserId = Guid.NewGuid(), IdentityIssuer = "issuer", IdentitySubject = "s2" });
                            transaction.UpdateUser(new User { UserId = user.UserId, IdentityIssuer = "issuer", IdentitySubject = "s3", RowVersion = userVersion });
                            transaction.DeleteSymbol("Custodian", "1", moved.RowVersion);
                            Check.That(partition.FindSymbol("Custodian", "1") == null, "the transaction reads its own writes");
                        }

                        Check.That(partition.Nodes.Count == 1 && partition.Accounts.Count == 0 && partition.Users.Count == 1, "inserted rows are gone");
                        Check.That(partition.FindSymbol("Custodian", "1") == symbol, "the deleted symbol is back");
                        Check.That(symbol.Node == a && symbol.NodeId == a.NodeId && symbol.RowVersion == symbolVersion, "the symbol's parent and version are restored");
                        Check.That(a.Symbols.Count == 1 && a.Symbols.Single() == symbol, "the parent's children are restored");
                        Check.That(user.IdentitySubject == "s1" && user.RowVersion == userVersion, "the updated user is restored");
                        Check.That(partition.FindUserByIdentity("issuer", "s1") == user, "the unique index points at the restored value");
                        Check.That(partition.FindUserByIdentity("issuer", "s2") == null && partition.FindUserByIdentity("issuer", "s3") == null, "no stray unique entries");
                    }

                    public static void SameRowsTouchedRepeatedlyRollBack()
                    {
                        var partition = NewPartition();
                        var a = partition.InsertNode(NewNode("A"));
                        var aVersion = a.RowVersion;
                        using (var transaction = partition.BeginTransaction())
                        {
                            var b = transaction.InsertNode(NewNode("B"));
                            var b1 = transaction.UpdateNode(new Node { NodeId = b.NodeId, DisplayName = "B1", FullName = "B1", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account, RowVersion = b.RowVersion })!;
                            transaction.UpdateNode(new Node { NodeId = b.NodeId, DisplayName = "B2", FullName = "B2", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account, RowVersion = b1.RowVersion });
                            transaction.DeleteNode(b.NodeId, b.RowVersion);
                            var s = transaction.InsertSymbol(new Symbol { Scheme = "X", Code = "1", NodeId = a.NodeId });
                            var c = transaction.InsertNode(NewNode("C"));
                            var s1 = transaction.UpdateSymbol(new Symbol { Scheme = "X", Code = "1", NodeId = c.NodeId, RowVersion = s.RowVersion })!;
                            transaction.DeleteSymbol("X", "1", s1.RowVersion);
                            transaction.InsertSymbol(new Symbol { Scheme = "X", Code = "1", NodeId = a.NodeId });
                            transaction.UpdateNode(new Node { NodeId = a.NodeId, DisplayName = "A1", FullName = "A1", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account, RowVersion = aVersion });
                        }

                        Check.That(partition.Nodes.Count == 1 && partition.Nodes.Single() == a, "only the committed node remains");
                        Check.That(a.DisplayName == "A" && a.RowVersion == aVersion, "the committed node is unchanged");
                        Check.That(partition.Symbols.Count == 0 && a.Symbols.Count == 0, "no symbol remains anywhere");
                    }

                    public static void FailedOperationsChangeNothing()
                    {
                        var partition = NewPartition();
                        Node a;
                        using (var transaction = partition.BeginTransaction())
                        {
                            a = transaction.InsertNode(NewNode("A"));
                            var missing = Guid.NewGuid();
                            try
                            {
                                transaction.InsertAccount(new Account { AccountId = missing, AccountType = AccountType.GroupAccount });
                                Check.That(false, "a missing parent is refused");
                            }
                            catch (ConstraintException)
                            {
                            }

                            Check.That(transaction.InsertNode(a) == a, "inserting a stored row again is a retry: it returns the stored row");
                            Check.That(transaction.InsertNode(new Node { NodeId = a.NodeId, DisplayName = "A", FullName = "A", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account }) == a, "an identical row with the same key is a retry too");
                            try
                            {
                                transaction.InsertNode(new Node { NodeId = a.NodeId, DisplayName = "Different", FullName = "A", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account });
                                Check.That(false, "a different row with the same key is refused");
                            }
                            catch (ConstraintException)
                            {
                            }

                            try
                            {
                                transaction.InsertNode(new Node { DisplayName = "x", FullName = "x", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account });
                                Check.That(false, "a row without its key is refused");
                            }
                            catch (ConstraintException)
                            {
                            }

                            transaction.InsertAccount(new Account { AccountId = a.NodeId, AccountType = AccountType.ManagedAccount });
                            transaction.Commit();
                        }

                        Check.That(partition.Nodes.Count == 1 && partition.Accounts.Count == 1 && a.Account != null, "only the successful changes were kept");
                    }

                    public static void StaleVersionsAreRefused()
                    {
                        var partition = NewPartition();
                        var a = partition.InsertNode(NewNode("A"));
                        using (var transaction = partition.BeginTransaction())
                        {
                            try
                            {
                                transaction.UpdateNode(new Node { NodeId = a.NodeId, DisplayName = "x", FullName = "x", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account, RowVersion = a.RowVersion - 1 });
                                Check.That(false, "a stale update is refused");
                            }
                            catch (DBConcurrencyException)
                            {
                            }

                            try
                            {
                                transaction.DeleteNode(a.NodeId, a.RowVersion - 1);
                                Check.That(false, "a stale delete is refused");
                            }
                            catch (DBConcurrencyException)
                            {
                            }

                            Check.That(transaction.DeleteNode(a.NodeId, a.RowVersion), "the current version deletes");
                            transaction.Commit();
                        }

                        Check.That(partition.Nodes.Count == 0, "the delete was kept");
                    }

                    public static void ReadersNeverSeeUncommittedChanges()
                    {
                        var partition = NewPartition();
                        var inserted = new ManualResetEventSlim();
                        var release = new ManualResetEventSlim();
                        var id = Guid.Empty;
                        Node? seen = new Node();
                        var writer = new Thread(() =>
                        {
                            using var transaction = partition.BeginTransaction();
                            id = transaction.InsertNode(NewNode("A")).NodeId;
                            inserted.Set();
                            release.Wait();
                        });
                        writer.Start();
                        inserted.Wait();
                        var reader = new Thread(() => seen = partition.FindNode(id));
                        reader.Start();
                        Check.That(!reader.Join(200), "the reader waits while the transaction is open");
                        release.Set();
                        writer.Join();
                        reader.Join();
                        Check.That(seen == null, "the reader never saw the rolled-back node");
                    }

                    public static void OneTransactionAtATime()
                    {
                        var partition = NewPartition();
                        using (var transaction = partition.BeginTransaction())
                        {
                            Check.Throws<InvalidOperationException>(() => { var nested = partition.BeginTransaction(); nested.Dispose(); }, "a nested transaction is refused");
                            Check.Throws<InvalidOperationException>(() => partition.InsertNode(NewNode("x")), "a shortcut inside a transaction is refused");
                            transaction.InsertNode(NewNode("A"));
                            transaction.Commit();
                        }

                        Check.That(Task.Run(() => { using var other = partition.BeginTransaction(); other.Commit(); }).Wait(2000), "the lock is free afterward");
                        Check.That(partition.Nodes.Count == 1, "the outer transaction committed");
                    }

                    public static void CopiesOfATransactionAreHarmless()
                    {
                        var partition = NewPartition();
                        var first = partition.BeginTransaction();
                        var copy = first;
                        var a = first.InsertNode(NewNode("A"));
                        copy.Commit();
                        first.Dispose();
                        copy.Dispose();
                        Check.That(partition.FindNode(a.NodeId) == a, "committing one copy and disposing both keeps the change");
                        try
                        {
                            first.InsertNode(NewNode("B"));
                            Check.That(false, "an ended transaction refuses changes");
                        }
                        catch (InvalidOperationException)
                        {
                        }

                        Check.That(Task.Run(() => { using var other = partition.BeginTransaction(); other.Commit(); }).Wait(2000), "the lock was released exactly once");
                        var empty = default(LedgerTransaction);
                        empty.Dispose();
                    }

                    public static void ChangesHappenOnlyThroughTransactions()
                    {
                        var partition = NewPartition();
                        var a = partition.InsertNode(NewNode("A"));
                        Check.Throws<InvalidOperationException>(() => partition.InsertNodeCore(NewNode("x")), "the internal operations require a transaction");
                        Check.Throws<InvalidOperationException>(() => partition.UpdateNode(a), "the stored row cannot be passed as its own changes");
                        var other = new Ledger().GetPartition(Guid.NewGuid());
                        Check.Throws<InvalidOperationException>(() => other.InsertNode(a), "a row cannot move to another tenant");
                    }

                    public static void ConcurrentTransactionsKeepEveryInvariant()
                    {
                        var partition = NewPartition();
                        var committed = 0;
                        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
                        var writers = Enumerable.Range(0, 8).Select(writerIndex => new Thread(() =>
                        {
                            var random = new Random(writerIndex);
                            for (var iteration = 0; iteration < 250; iteration++)
                            {
                                using var transaction = partition.BeginTransaction();
                                var node = transaction.InsertNode(NewNode("N"));
                                transaction.InsertAccount(new Account { AccountId = node.NodeId, AccountType = AccountType.ManagedAccount });
                                transaction.InsertSymbol(new Symbol { Scheme = "W" + writerIndex, Code = iteration.ToString(), NodeId = node.NodeId });
                                if (random.Next(2) == 0)
                                {
                                    transaction.Commit();
                                    Interlocked.Increment(ref committed);
                                }
                            }
                        })).ToList();
                        var readers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
                        {
                            for (var pass = 0; pass < 250; pass++)
                            {
                                lock (partition.SyncRoot)
                                {
                                    foreach (var node in partition.Nodes)
                                    {
                                        if (node.Account == null || node.Account.Node != node || node.Symbols.Count != 1 || node.Symbols.Single().Node != node)
                                        {
                                            failures.Enqueue("a reader saw part of a transaction");
                                        }
                                    }

                                    if (partition.Accounts.Count != partition.Nodes.Count || partition.Symbols.Count != partition.Nodes.Count)
                                    {
                                        failures.Enqueue("a reader saw tables that disagree");
                                    }
                                }
                            }
                        })).ToList();

                        writers.ForEach(thread => thread.Start());
                        readers.ForEach(thread => thread.Start());
                        writers.ForEach(thread => thread.Join());
                        readers.ForEach(thread => thread.Join());
                        Check.That(failures.IsEmpty, failures.FirstOrDefault() ?? string.Empty);
                        Check.That(partition.Nodes.Count == committed && partition.Accounts.Count == committed && partition.Symbols.Count == committed, "exactly the committed transactions remain");
                    }

                    public static void ChildSetsAreAllocatedWithTheFirstChild()
                    {
                        var partition = NewPartition();
                        var a = partition.InsertNode(NewNode("A"));
                        Check.That(SetOf(a, "symbols") == null && SetOf(a, "edgesByParent") == null && a.Symbols.Count == 0, "a new node carries no sets");
                        partition.InsertSymbol(new Symbol { Scheme = "X", Code = "1", NodeId = a.NodeId });
                        Check.That(SetOf(a, "symbols") != null && SetOf(a, "edgesByParent") == null && a.Symbols.Count == 1, "only the set that has a child exists");
                    }
                }
            }
            """;

        private const string ClientScenarios = Common + """
            namespace Scenarios
            {
                using AtlasLedger.Model;
                using AtlasLedger.Model.Replica;
            
                public static class Client
                {
                    public static void AppliedChangesRollBackAsAUnit()
                    {
                        var partition = new Ledger().GetPartition(Guid.NewGuid());
                        var nodeId = Guid.NewGuid();
                        var node = partition.ApplyNode(new NodeDto { NodeId = nodeId, DisplayName = "A", FullName = "A", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account, RowVersion = 1 });
                        partition.ApplySymbol(new SymbolDto { Scheme = "X", Code = "1", NodeId = nodeId, RowVersion = 2 });

                        using (var transaction = partition.BeginTransaction())
                        {
                            transaction.ApplyNode(new NodeDto { NodeId = nodeId, DisplayName = "B", FullName = "B", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account, RowVersion = 3 });
                            transaction.ApplyAccount(new AccountDto { AccountId = nodeId, AccountType = AccountType.ManagedAccount, RowVersion = 4 });
                            transaction.RemoveNode(nodeId);
                            Check.That(partition.Symbols.Count == 0, "removing the node cascades in the mirror");
                        }

                        Check.That(partition.FindNode(nodeId) == node && node.DisplayName == "A" && node.RowVersion == 1, "the node is restored");
                        Check.That(node.Account == null && partition.Accounts.Count == 0, "the applied account is gone");
                        Check.That(node.Symbols.Count == 1 && partition.Symbols.Single().Node == node, "the cascaded symbol is back and linked");
                    }
                }
            }
            """;

        private const string MarketModel = """
            {
              "access": { "read": ["Market.Read"], "write": ["Market.Write"] },
              "datatypes": {
                "code": { "maxLength": 16, "type": "System.String" },
                "guid": "System.Guid",
                "price": { "precision": 18, "scale": 6, "type": "System.Decimal" }
              },
              "name": "Market",
              "namespace": "Test.Market",
              "tables": {
                "Listing": {
                  "columns": { "Code": "code", "Name": "code", "Scheme": "code" },
                  "key": ["Scheme", "Code"]
                },
                "Trade": {
                  "columns": { "Code": "code", "Price": "price", "Scheme": "code", "TradeId": "guid" },
                  "key": ["TradeId"],
                  "references": { "Listing": { "columns": ["Scheme", "Code"] } }
                },
                "Watch": {
                  "columns": { "Code": "code?", "Scheme": "code?", "WatchId": "guid" },
                  "key": ["WatchId"],
                  "references": { "Listing": { "columns": ["Scheme", "Code"] } }
                }
              },
              "version": "1.0"
            }
            """;

        private const string MarketScenarios = Common + """
            namespace Scenarios
            {
                using Test.Market;
                using Test.Market.Primary;

                public static class Composite
                {
                    public static void ReferencesToCompositeKeysWork()
                    {
                        var partition = new Market().GetPartition(Guid.NewGuid());
                        var listing = partition.InsertListing(new Listing { Scheme = "XNYS", Code = "IBM", Name = "IBM" });
                        var trade = partition.InsertTrade(new Trade { TradeId = Guid.NewGuid(), Scheme = "XNYS", Code = "IBM", Price = 100m });
                        Check.That(trade.Listing == listing && listing.Trades.Single() == trade, "a required composite reference links both ways");
                        Check.That(partition.FindListing("XNYS", "IBM") == listing, "a composite key finds its row");
                        Check.Throws<ConstraintException>(() => partition.InsertTrade(new Trade { TradeId = Guid.NewGuid(), Scheme = "XNYS", Code = "NONE", Price = 1m }), "a missing composite parent is refused");

                        var watch = partition.InsertWatch(new Watch { WatchId = Guid.NewGuid() });
                        Check.That(watch.Listing == null && listing.Watches.Count == 0, "an unset optional composite reference links nothing");
                        var linked = partition.UpdateWatch(new Watch { WatchId = watch.WatchId, Scheme = "XNYS", Code = "IBM", RowVersion = watch.RowVersion })!;
                        Check.That(watch.Listing == listing && listing.Watches.Single() == watch, "setting every column links it");
                        partition.UpdateWatch(new Watch { WatchId = watch.WatchId, Scheme = "XNYS", Code = null, RowVersion = linked.RowVersion });
                        Check.That(watch.Listing == null && listing.Watches.Count == 0, "a partly set composite reference is unset");
                        Check.Throws<ConstraintException>(() => partition.DeleteListing("XNYS", "IBM", listing.RowVersion), "a parent with children cannot be deleted");

                        using (var transaction = partition.BeginTransaction())
                        {
                            transaction.DeleteTrade(trade.TradeId, trade.RowVersion);
                            transaction.DeleteListing("XNYS", "IBM", listing.RowVersion);
                            Check.That(partition.FindListing("XNYS", "IBM") == null, "deleted within the transaction");
                        }

                        Check.That(partition.FindListing("XNYS", "IBM") == listing && listing.Trades.Single() == trade && trade.Listing == listing, "rollback restores composite links");
                    }
                }
            }
            """;

        /// <summary>
        /// Committing keeps every change in a transaction, with every link.
        /// </summary>
        [Fact]
        public void CommitKeepsEveryChange() => Run(Server, "Server", nameof(this.CommitKeepsEveryChange));

        /// <summary>
        /// Disposing without committing undoes inserts, updates (including moves between parents and unique values), and
        /// deletes, across tables, restoring versions, indexes, and links.
        /// </summary>
        [Fact]
        public void DisposeWithoutCommitUndoesEverything() => Run(Server, "Server", nameof(this.DisposeWithoutCommitUndoesEverything));

        /// <summary>
        /// Rows inserted, updated several times, deleted, and re-inserted with the same key in one transaction roll back
        /// cleanly: the case the old design's oldest-first rollback got wrong.
        /// </summary>
        [Fact]
        public void SameRowsTouchedRepeatedlyRollBack() => Run(Server, "Server", nameof(this.SameRowsTouchedRepeatedlyRollBack));

        /// <summary>
        /// A failed operation changes nothing, and the transaction stays usable.
        /// </summary>
        [Fact]
        public void FailedOperationsChangeNothing() => Run(Server, "Server", nameof(this.FailedOperationsChangeNothing));

        /// <summary>
        /// Updates and deletes at a stale version are refused.
        /// </summary>
        [Fact]
        public void StaleVersionsAreRefused() => Run(Server, "Server", nameof(this.StaleVersionsAreRefused));

        /// <summary>
        /// Another thread waits while a transaction is open, and never sees a change that is rolled back.
        /// </summary>
        [Fact]
        public void ReadersNeverSeeUncommittedChanges() => Run(Server, "Server", nameof(this.ReadersNeverSeeUncommittedChanges));

        /// <summary>
        /// Only one transaction can be open on a partition; a shortcut inside one is refused; the lock is free afterward.
        /// </summary>
        [Fact]
        public void OneTransactionAtATime() => Run(Server, "Server", nameof(this.OneTransactionAtATime));

        /// <summary>
        /// Copying the transaction struct is harmless: commit through one copy, dispose both, and the lock is released once.
        /// </summary>
        [Fact]
        public void CopiesOfATransactionAreHarmless() => Run(Server, "Server", nameof(this.CopiesOfATransactionAreHarmless));

        /// <summary>
        /// Changes can be made only through transactions, never by passing the stored row as its own changes, and a row
        /// cannot move to another tenant.
        /// </summary>
        [Fact]
        public void ChangesHappenOnlyThroughTransactions() => Run(Server, "Server", nameof(this.ChangesHappenOnlyThroughTransactions));

        /// <summary>
        /// Eight writers commit or roll back at random while two readers walk the graph: readers never see part of a
        /// transaction, and exactly the committed transactions remain.
        /// </summary>
        [Fact]
        public void ConcurrentTransactionsKeepEveryInvariant() => Run(Server, "Server", nameof(this.ConcurrentTransactionsKeepEveryInvariant));

        /// <summary>
        /// A parent's set of children is allocated with its first child.
        /// </summary>
        [Fact]
        public void ChildSetsAreAllocatedWithTheFirstChild() => Run(Server, "Server", nameof(this.ChildSetsAreAllocatedWithTheFirstChild));

        /// <summary>
        /// The client mirror applies and removes rows in transactions too, so a server transaction can be applied as a unit.
        /// </summary>
        [Fact]
        public void ClientChangesRollBackAsAUnit() => Run(Client, "Client", "AppliedChangesRollBackAsAUnit");

        /// <summary>
        /// References to composite keys, required and optional, link, unlink, and roll back.
        /// </summary>
        [Fact]
        public void ReferencesToCompositeKeysWork() => Run(Market, "Composite", nameof(this.ReferencesToCompositeKeysWork));

        /// <summary>
        /// The compiler refuses to hold a transaction across an await, so nothing waits on I/O while holding a tenant's lock.
        /// </summary>
        [Fact]
        public void TransactionsCannotSpanAnAwait()
        {
            const string Source = """
                namespace Scenarios
                {
                    using System.Threading.Tasks;
                    using AtlasLedger.Model;

                    public static class Await
                    {
                        public static async Task HoldAcrossAwait()
                        {
                            using var transaction = new Ledger().GetPartition(System.Guid.NewGuid()).BeginTransaction();
                            await Task.Delay(1);
                            transaction.Commit();
                        }
                    }
                }
                """;

            var run = GeneratorTests.Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true, extraSource: Source);

            // The compiler reports this while lowering async methods, which happens when it emits code.
            using var stream = new MemoryStream();
            var result = run.Output.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "CS4007");
        }

        internal static void Run(Lazy<Assembly> assembly, string type, string method)
        {
            var scenario = assembly.Value.GetType($"Scenarios.{type}")!.GetMethod(method)!;
            try
            {
                scenario.Invoke(null, null);
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw new Xunit.Sdk.XunitException($"{method}: {exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
            }
        }

        internal static Assembly Compile(string model, string role, string scenarios, bool includeEnums = true, bool withRules = false)
        {
            var run = GeneratorTests.Run(model, role, includeEnums, extraSource: scenarios, withRules: withRules);
            Assert.Empty(run.Result.Diagnostics);
            using var stream = new MemoryStream();
            var result = run.Output.Emit(stream);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
            return Assembly.Load(stream.ToArray());
        }
    }
}
