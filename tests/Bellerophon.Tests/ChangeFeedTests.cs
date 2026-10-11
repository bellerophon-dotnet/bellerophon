// <copyright file="ChangeFeedTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System;
    using System.Reflection;
    using Xunit;

    /// <summary>
    /// Tests the change feed a rules engine (and later, client sync) reads: committed changes after a sequence, coalesced and
    /// in commit order, deletions included; snapshots; the journal's horizon; and waiting for a commit.  Each scenario is C#
    /// compiled together with the generated code.
    /// </summary>
    public class ChangeFeedTests
    {
        private static readonly Lazy<Assembly> Server = new Lazy<Assembly>(() => TransactionTests.Compile(Fixture.Read("bellerophon.json"), "Primary", Scenarios));

        private const string Scenarios = TransactionTests.Common + """
            namespace Scenarios
            {
                using System.Collections.Generic;
                using AtlasLedger.Model;
                using AtlasLedger.Model.Primary;

                public static class Feed
                {
                    public static void NewPartitionsAreAnnounced()
                    {
                        var ledger = new Ledger();
                        var created = new List<Guid>();
                        ledger.PartitionCreated += (_, partition) => created.Add(partition.TenantId);
                        var tenantId = Guid.NewGuid();
                        var first = ledger.GetPartition(tenantId);
                        var again = ledger.GetPartition(tenantId);
                        ledger.FindPartition(Guid.NewGuid());
                        Check.That(object.ReferenceEquals(first, again) && created.Count == 1 && created[0] == tenantId, "a tenant's partition is announced once, when it is created, and finding one creates nothing");
                        ledger.DiscardPartition(tenantId);
                        ledger.GetPartition(tenantId);
                        Check.That(created.Count == 2, "a discarded tenant's new partition is announced again");
                    }

                    private static Node NewNode(string name) => new Node { NodeId = Guid.NewGuid(), DisplayName = name, FullName = name, NodeStatus = NodeStatus.Active, NodeType = NodeType.Account };

                    private static Node Renamed(Node node, string name) => new Node { NodeId = node.NodeId, DisplayName = name, FullName = name, NodeStatus = NodeStatus.Active, NodeType = NodeType.Account, RowVersion = node.RowVersion };

                    private static LedgerPartition NewPartition() => new Ledger().GetPartition(Guid.NewGuid());

                    public static void ChangesAreCoalescedToTheLatestState()
                    {
                        var partition = NewPartition();
                        var node = partition.InsertNode(NewNode("A"));
                        partition.UpdateNode(Renamed(node, "B"));
                        partition.UpdateNode(Renamed(node, "C"));
                        var changes = partition.ReadChangesSince(0);
                        Check.That(changes.IsComplete && changes.ThroughSequence == partition.CommittedSequence, "the read runs through the last commit");
                        Check.That(changes.ChangedNodes.Count == 1 && changes.ChangedNodes[0].DisplayName == "C", "three changes to a row arrive once, in its latest state");
                    }

                    public static void ReadsStartAfterTheReadersSequence()
                    {
                        var partition = NewPartition();
                        partition.InsertNode(NewNode("A"));
                        var mark = partition.CommittedSequence;
                        partition.InsertNode(NewNode("B"));
                        var changes = partition.ReadChangesSince(mark);
                        Check.That(changes.FromSequence == mark && changes.ChangedNodes.Count == 1 && changes.ChangedNodes[0].DisplayName == "B", "only changes after the mark");
                        Check.That(partition.ReadChangesSince(partition.CommittedSequence).IsEmpty, "nothing after the last commit");
                    }

                    public static void DeletesAdvanceTheSequenceAndArriveAsDeletions()
                    {
                        var partition = NewPartition();
                        var node = partition.InsertNode(NewNode("A"));
                        var symbol = partition.InsertSymbol(new Symbol { Scheme = "X", Code = "1", NodeId = node.NodeId });
                        var mark = partition.CommittedSequence;
                        partition.DeleteSymbol("X", "1", symbol.RowVersion);
                        Check.That(partition.CommittedSequence > mark, "a transaction that only deletes still advances the sequence");
                        var changes = partition.ReadChangesSince(mark);
                        Check.That(changes.DeletedSymbols.Count == 1 && changes.DeletedSymbols[0].Code == "1" && changes.ChangedSymbols.Count == 0, "the deletion arrives as a deletion");
                    }

                    public static void AKeyDeletedAndInsertedAgainIsAChange()
                    {
                        var partition = NewPartition();
                        var node = partition.InsertNode(NewNode("A"));
                        var symbol = partition.InsertSymbol(new Symbol { Scheme = "X", Code = "1", NodeId = node.NodeId });
                        var mark = partition.CommittedSequence;
                        using (var transaction = partition.BeginTransaction())
                        {
                            transaction.DeleteSymbol("X", "1", symbol.RowVersion);
                            transaction.InsertSymbol(new Symbol { Scheme = "X", Code = "1", NodeId = node.NodeId });
                            transaction.Commit();
                        }

                        var changes = partition.ReadChangesSince(mark);
                        Check.That(changes.ChangedSymbols.Count == 1 && changes.DeletedSymbols.Count == 0, "a reader sees the key changed, not deleted after the change");
                    }

                    public static void RolledBackTransactionsAreNeverRead()
                    {
                        var partition = NewPartition();
                        var mark = partition.CommittedSequence;
                        using (var transaction = partition.BeginTransaction())
                        {
                            transaction.InsertNode(NewNode("A"));
                        }

                        Check.That(partition.ReadChangesSince(mark).IsEmpty, "nothing uncommitted is read");
                    }

                    public static void ALimitDefersLaterChanges()
                    {
                        var partition = NewPartition();
                        var node = partition.InsertNode(NewNode("A"));
                        var limit = partition.CommittedSequence;
                        partition.UpdateNode(Renamed(node, "B"));
                        var early = partition.ReadChangesSince(0, limit);
                        Check.That(early.ThroughSequence == limit && early.ChangedNodes.Count == 0, "a row whose latest change is past the limit waits");
                        var late = partition.ReadChangesSince(early.ThroughSequence);
                        Check.That(late.ChangedNodes.Count == 1 && late.ChangedNodes[0].DisplayName == "B", "the next read delivers it");
                    }

                    public static void TheJournalKeepsOneEntryPerRow()
                    {
                        var partition = NewPartition();
                        var node = partition.InsertNode(NewNode("A"));
                        for (var index = 0; index < 3000; index++)
                        {
                            node = partition.UpdateNode(Renamed(node, "N" + index))!;
                        }

                        Check.That(partition.JournalCount == 1, $"the journal holds {partition.JournalCount} live entries for one row");
                        Check.That(partition.ReadChangesSince(0).ChangedNodes[0].DisplayName == "N2999", "the latest state survives compaction");
                    }

                    public static void TrimmingSendsReadersBehindItToASnapshot()
                    {
                        var partition = NewPartition();
                        partition.InsertNode(NewNode("A"));
                        var old = partition.CommittedSequence;
                        partition.InsertNode(NewNode("B"));
                        var horizon = partition.CommittedSequence;
                        partition.InsertNode(NewNode("C"));
                        partition.TrimJournal(horizon);
                        Check.That(partition.JournalHorizon == horizon, "the horizon moves");
                        Check.That(!partition.ReadChangesSince(old).IsComplete, "a reader behind the horizon must take a snapshot");
                        var current = partition.ReadChangesSince(horizon);
                        Check.That(current.IsComplete && current.ChangedNodes.Count == 1 && current.ChangedNodes[0].DisplayName == "C", "a reader at the horizon reads on");
                        var snapshot = partition.TakeSnapshot();
                        Check.That(snapshot.ChangedNodes.Count == 3 && snapshot.ThroughSequence == partition.CommittedSequence, "a snapshot holds every row, through the last commit");
                    }

                    public static void SnapshotsAndReadsHappenBetweenTransactions()
                    {
                        var partition = NewPartition();
                        using (var transaction = partition.BeginTransaction())
                        {
                            transaction.InsertNode(NewNode("A"));
                            Check.Throws<InvalidOperationException>(() => partition.TakeSnapshot(), "no snapshot inside a transaction");
                            Check.Throws<InvalidOperationException>(() => partition.ReadChangesSince(0), "no read inside a transaction");
                            transaction.Commit();
                        }
                    }

                    public static void ALoadedPartitionsHistoryStartsAtItsWatermark()
                    {
                        var source = NewPartition();
                        source.InsertNode(NewNode("A"));
                        var snapshot = source.TakeSnapshot();
                        var loaded = new Ledger().GetPartition(source.TenantId);
                        loaded.BeginLoad();
                        foreach (var dto in snapshot.ChangedNodes)
                        {
                            loaded.LoadNode(dto);
                        }

                        loaded.CompleteLoad(snapshot.ThroughSequence);
                        Check.That(!loaded.ReadChangesSince(0).IsComplete, "changes before the load were never journaled");
                        loaded.InsertNode(NewNode("B"));
                        var changes = loaded.ReadChangesSince(snapshot.ThroughSequence);
                        Check.That(changes.IsComplete && changes.ChangedNodes.Count == 1, "changes after the load are");
                    }

                    public static void WaitingForACommitWakesOnTheNextCommit()
                    {
                        var partition = NewPartition();
                        var mark = partition.CommittedSequence;
                        var waits = new List<Task>();
                        for (var index = 0; index < 10000; index++)
                        {
                            waits.Add(partition.WaitForCommitAsync(mark));
                        }

                        Check.That(!waits[0].Wait(50), "nothing to wake for yet");
                        partition.InsertNode(NewNode("A"));
                        Check.That(Task.WhenAll(waits).Wait(2000), "one commit wakes every waiter");
                        Check.That(partition.WaitForCommitAsync(mark).IsCompleted, "a reader behind the last commit does not wait");
                    }

                    public static void FactsArriveParentsFirstAndRetractionsChildrenFirst()
                    {
                        var partition = NewPartition();
                        var node = partition.InsertNode(NewNode("A"));
                        partition.InsertAccount(new Account { AccountId = node.NodeId, AccountType = AccountType.ManagedAccount });
                        var user = partition.InsertUser(new User { UserId = Guid.NewGuid(), IdentityIssuer = "issuer", IdentitySubject = "subject" });
                        partition.InsertMember(new Member { UserId = user.UserId, UserType = UserType.Individual });
                        var facts = partition.ReadChangesSince(0).ToFacts().ToList();
                        var nodeAt = facts.FindIndex(fact => fact.Fact is NodeFact);
                        var accountAt = facts.FindIndex(fact => fact.Fact is AccountFact);
                        Check.That(nodeAt >= 0 && accountAt > nodeAt, "a parent's fact comes before its child's");
                        Check.That(facts.All(fact => fact.Fact.GetType().Name is not ("MemberFact" or "UserFact")), "tables marked \"facts\": false, and the built-in User, stay out of working memory");

                        var mark = partition.CommittedSequence;
                        using (var transaction = partition.BeginTransaction())
                        {
                            // The account blocks deleting its node, so it goes first; the feed still retracts children first.
                            transaction.DeleteAccount(node.NodeId, partition.FindAccount(node.NodeId)!.RowVersion);
                            transaction.DeleteNode(node.NodeId, partition.FindNode(node.NodeId)!.RowVersion);
                            transaction.Commit();
                        }
                        var retractions = partition.ReadChangesSince(mark).ToFacts().ToList();
                        Check.That(retractions.All(fact => fact.IsRetraction), "deletions are retractions");
                        Check.That(retractions.FindIndex(fact => fact.Fact is AccountFact) < retractions.FindIndex(fact => fact.Fact is NodeFact), "a child's retraction comes before its parent's");
                    }

                    public static void FactsAreEqualByKeyAlone()
                    {
                        var first = new SymbolFact(new SymbolDto { Scheme = "X", Code = "1", NodeId = Guid.NewGuid(), RowVersion = 1 });
                        var second = new SymbolFact(new SymbolDto { Scheme = "X", Code = "1", NodeId = Guid.NewGuid(), RowVersion = 9 });
                        var other = new SymbolFact(new SymbolDto { Scheme = "X", Code = "2", NodeId = first.NodeId, RowVersion = 1 });
                        Check.That(first.Equals(second) && first.GetHashCode() == second.GetHashCode(), "the same key is the same fact, whatever its values");
                        Check.That(!first.Equals(other), "a different key is a different fact");
                        var memory = new HashSet<object> { first };
                        Check.That(memory.Contains(second), "a working memory keyed by equality finds the old fact from the new one");
                        var price = new PriceFact(new Price { Scheme = "X", Code = "1", Last = 5m });
                        Check.That(price.Last == 5m && price.Equals(new PriceFact(new Price { Scheme = "X", Code = "1", Last = 6m })), "latest values become facts the same way");
                    }
                }
            }
            """;

        /// <summary>
        /// The ledger announces each new partition once, so per-tenant services start without polling.
        /// </summary>
        [Fact]
        public void NewPartitionsAreAnnounced() => Run(nameof(this.NewPartitionsAreAnnounced));

        /// <summary>
        /// Several changes to a row arrive once, in its latest state.
        /// </summary>
        [Fact]
        public void ChangesAreCoalescedToTheLatestState() => Run(nameof(this.ChangesAreCoalescedToTheLatestState));

        /// <summary>
        /// A read returns only the changes after the reader's sequence.
        /// </summary>
        [Fact]
        public void ReadsStartAfterTheReadersSequence() => Run(nameof(this.ReadsStartAfterTheReadersSequence));

        /// <summary>
        /// A transaction that only deletes advances the sequence, so its deletions are read.
        /// </summary>
        [Fact]
        public void DeletesAdvanceTheSequenceAndArriveAsDeletions() => Run(nameof(this.DeletesAdvanceTheSequenceAndArriveAsDeletions));

        /// <summary>
        /// A key deleted and inserted again is read as changed.
        /// </summary>
        [Fact]
        public void AKeyDeletedAndInsertedAgainIsAChange() => Run(nameof(this.AKeyDeletedAndInsertedAgainIsAChange));

        /// <summary>
        /// Nothing rolled back is ever read.
        /// </summary>
        [Fact]
        public void RolledBackTransactionsAreNeverRead() => Run(nameof(this.RolledBackTransactionsAreNeverRead));

        /// <summary>
        /// A read limited to a sequence (the durable watermark, for client sync) leaves later changes for the next read.
        /// </summary>
        [Fact]
        public void ALimitDefersLaterChanges() => Run(nameof(this.ALimitDefersLaterChanges));

        /// <summary>
        /// The journal keeps one live entry per changed row, however often it changes.
        /// </summary>
        [Fact]
        public void TheJournalKeepsOneEntryPerRow() => Run(nameof(this.TheJournalKeepsOneEntryPerRow));

        /// <summary>
        /// A reader behind a trimmed journal must take a snapshot; one at the horizon reads on.
        /// </summary>
        [Fact]
        public void TrimmingSendsReadersBehindItToASnapshot() => Run(nameof(this.TrimmingSendsReadersBehindItToASnapshot));

        /// <summary>
        /// Snapshots and reads are refused inside a transaction, so they never include uncommitted changes.
        /// </summary>
        [Fact]
        public void SnapshotsAndReadsHappenBetweenTransactions() => Run(nameof(this.SnapshotsAndReadsHappenBetweenTransactions));

        /// <summary>
        /// A loaded partition's journal starts at its watermark.
        /// </summary>
        [Fact]
        public void ALoadedPartitionsHistoryStartsAtItsWatermark() => Run(nameof(this.ALoadedPartitionsHistoryStartsAtItsWatermark));

        /// <summary>
        /// One commit wakes every waiter, however many there are.
        /// </summary>
        [Fact]
        public void WaitingForACommitWakesOnTheNextCommit() => Run(nameof(this.WaitingForACommitWakesOnTheNextCommit));

        /// <summary>
        /// Facts arrive parents first and retractions children first; excluded tables stay out of working memory.
        /// </summary>
        [Fact]
        public void FactsArriveParentsFirstAndRetractionsChildrenFirst() => Run(nameof(this.FactsArriveParentsFirstAndRetractionsChildrenFirst));

        /// <summary>
        /// Facts are equal by key alone, as a Rete engine requires.
        /// </summary>
        [Fact]
        public void FactsAreEqualByKeyAlone() => Run(nameof(this.FactsAreEqualByKeyAlone));

        private static void Run(string method) => TransactionTests.Run(Server, "Feed", method);
    }
}
