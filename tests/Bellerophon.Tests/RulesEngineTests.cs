// <copyright file="RulesEngineTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System;
    using System.Reflection;
    using Xunit;

    /// <summary>
    /// Tests the compliance engine end to end: the generated rules tenant feeding a session, cycles, decisions committed in
    /// one transaction, conflated latest values, refused decisions, failed cycles, and the host.  The session is a small
    /// hand-written rule set standing in for NRules; each scenario is C# compiled together with the generated code.
    /// </summary>
    public class RulesEngineTests
    {
        private static readonly Lazy<Assembly> Server = new Lazy<Assembly>(() => TransactionTests.Compile(Fixture.Read("bellerophon.json"), "Primary", Scenarios, withRules: true));

        private const string Scenarios = TransactionTests.Common + """
            namespace Scenarios
            {
                using System.Collections.Generic;
                using AtlasLedger.Model;
                using AtlasLedger.Model.Primary;
                using Bellerophon.Rules;

                // One rule: every account has a custodian symbol, or an open alert. Equal facts replace each other, as in NRules.
                public sealed class TestSession : IRulesSession
                {
                    private readonly Dictionary<object, object> memory = new Dictionary<object, object>();
                    private readonly LedgerRuleActions actions;

                    public TestSession(IRulesTenant tenant)
                    {
                        this.actions = (LedgerRuleActions)tenant.GetService(typeof(LedgerRuleActions))!;
                    }

                    public static bool FailNextFire { get; set; }

                    public static Action<LedgerTransaction>? ExtraDecision { get; set; }

                    // Adds a decision to every cycle that is always refused, like a rule that breaks a constraint each time.
                    public static Func<LedgerTransaction, bool>? RefuseEveryCycle { get; set; }

                    public List<FactChange> Applied { get; } = new List<FactChange>();

                    public void Apply(IReadOnlyList<FactChange> changes)
                    {
                        foreach (var change in changes)
                        {
                            this.Applied.Add(change);
                            this.memory.Remove(change.Fact);
                            if (!change.IsRetraction)
                            {
                                this.memory.Add(change.Fact, change.Fact);
                            }
                        }
                    }

                    public int Fire()
                    {
                        var fired = 0;
                        var symbols = this.memory.Values.OfType<SymbolFact>().ToList();
                        var alerts = this.memory.Values.OfType<AlertFact>().ToDictionary(alert => alert.NodeId);
                        foreach (var account in this.memory.Values.OfType<AccountFact>())
                        {
                            var isSatisfied = symbols.Any(symbol => symbol.NodeId == account.AccountId && symbol.Scheme == "Custodian");
                            var isOpen = alerts.TryGetValue(account.AccountId, out var alert);
                            if (!isSatisfied && !isOpen)
                            {
                                var nodeId = account.AccountId;
                                this.actions.Add(transaction => transaction.InsertAlert(new Alert { AlertId = Guid.NewGuid(), NodeId = nodeId, Rule = "MissingCustodian", Message = "No custodian." }));
                                fired++;
                            }
                            else if (isSatisfied && isOpen)
                            {
                                var (alertId, version) = (alert!.AlertId, alert.RowVersion);
                                this.actions.Add(transaction => transaction.DeleteAlert(alertId, version));
                                fired++;
                            }
                        }

                        if (ExtraDecision is { } extra)
                        {
                            this.actions.Add(extra);
                            ExtraDecision = null;
                        }

                        if (RefuseEveryCycle is { } refuse)
                        {
                            this.actions.Add(transaction => refuse(transaction));
                        }

                        if (FailNextFire)
                        {
                            FailNextFire = false;
                            throw new InvalidOperationException("A rule failed.");
                        }

                        return fired;
                    }
                }

                public sealed class TestSessionFactory : IRulesSessionFactory
                {
                    public List<TestSession> Sessions { get; } = new List<TestSession>();

                    public IRulesSession CreateSession(IRulesTenant tenant)
                    {
                        var session = new TestSession(tenant);
                        this.Sessions.Add(session);
                        return session;
                    }
                }

                public static class Engine
                {
                    private static LedgerPartition NewPartition() => new Ledger().GetPartition(Guid.NewGuid());

                    private static Guid AddAccount(LedgerPartition partition, bool withCustodian)
                    {
                        var node = partition.InsertNode(new Node { NodeId = Guid.NewGuid(), DisplayName = "A", FullName = "A", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account });
                        partition.InsertAccount(new Account { AccountId = node.NodeId, AccountType = AccountType.ManagedAccount });
                        if (withCustodian)
                        {
                            partition.InsertSymbol(new Symbol { Scheme = "Custodian", Code = node.NodeId.ToString("N").Substring(0, 8), NodeId = node.NodeId });
                        }

                        return node.NodeId;
                    }

                    public static void TheFirstCycleLoadsASnapshotAndCommitsTheDecisions()
                    {
                        var partition = NewPartition();
                        var accountId = AddAccount(partition, withCustodian: false);
                        AddAccount(partition, withCustodian: true);
                        using var tenant = new LedgerRulesTenant(partition);
                        var engine = new TenantRulesEngine(tenant, new TestSessionFactory());
                        var first = engine.RunCycle();
                        Check.That(first.IsRebuild && first.RulesFired == 1 && first.CommittedSequence > first.ThroughSequence, "the first cycle starts from a snapshot and commits one decision");
                        Check.That(partition.Alerts.Single().NodeId == accountId, "the decision is in the ledger");
                        var second = engine.RunCycle();
                        Check.That(!second.IsRebuild && second.RulesFired == 0 && second.CommittedSequence == 0 && second.FactChanges == 1, "the next cycle sees its own decision as a fact and does nothing more");
                        Check.That(engine.AppliedSequence == partition.CommittedSequence, "the engine has caught up");
                    }

                    public static void ChangesReachTheRulesInOrder()
                    {
                        var partition = NewPartition();
                        var accountId = AddAccount(partition, withCustodian: false);
                        using var tenant = new LedgerRulesTenant(partition);
                        var engine = new TenantRulesEngine(tenant, new TestSessionFactory());
                        engine.RunCycle();
                        engine.RunCycle();
                        partition.InsertSymbol(new Symbol { Scheme = "Custodian", Code = "1", NodeId = accountId });
                        var cleared = engine.RunCycle();
                        Check.That(cleared.RulesFired == 1 && partition.Alerts.Count == 0, "a new custodian symbol clears the alert in the next cycle");
                        var settled = engine.RunCycle();
                        Check.That(settled.RulesFired == 0, "and the rules settle");
                    }

                    public static void LatestValuesAreConflated()
                    {
                        var partition = NewPartition();
                        using var tenant = new LedgerRulesTenant(partition);
                        var factory = new TestSessionFactory();
                        var engine = new TenantRulesEngine(tenant, factory);
                        engine.RunCycle();
                        for (var tick = 1; tick <= 500; tick++)
                        {
                            partition.PublishPrice(new Price { Scheme = "Custodian", Code = "1", Last = tick, AsOf = DateTimeOffset.UtcNow });
                        }

                        Check.That(tenant.WaitForChangesAsync(engine.AppliedSequence, default).IsCompleted, "a published value wakes the engine");
                        var cycle = engine.RunCycle();
                        var applied = factory.Sessions.Single().Applied.Where(change => change.Fact is PriceFact).ToList();
                        Check.That(cycle.FactChanges == 1 && applied.Count == 1 && ((PriceFact)applied[0].Fact).Last == 500m, "500 ticks are one fact, at the latest value");
                        partition.RemovePrice("Custodian", "1");
                        var removed = engine.RunCycle();
                        Check.That(removed.FactChanges == 1 && factory.Sessions.Single().Applied.Last().IsRetraction, "a removed value is retracted");
                        Check.That(!tenant.WaitForChangesAsync(engine.AppliedSequence, default).Wait(50), "with nothing new, the engine sleeps");
                    }

                    public static void RefusedDecisionsCommitNothing()
                    {
                        var partition = NewPartition();
                        var accountId = AddAccount(partition, withCustodian: false);
                        using var tenant = new LedgerRulesTenant(partition);
                        var engine = new TenantRulesEngine(tenant, new TestSessionFactory());
                        var node = partition.FindNode(accountId)!;
                        var before = partition.CommittedSequence;
                        TestSession.ExtraDecision = transaction => transaction.UpdateNode(new Node { NodeId = node.NodeId, DisplayName = "B", FullName = "B", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account, RowVersion = node.RowVersion - 1 });
                        var cycle = engine.RunCycle();
                        Check.That(cycle.RejectedActions is System.Data.DBConcurrencyException, "a stale decision is refused");
                        Check.That(partition.CommittedSequence == before && partition.Alerts.Count == 0, "and nothing the cycle decided is committed");
                        var retry = engine.RunCycle();
                        Check.That(retry.RulesFired == 1 && partition.Alerts.Count == 1, "the next cycle decides again");
                    }

                    public static void RepeatedRefusalsBackOff()
                    {
                        var partition = NewPartition();
                        var accountId = AddAccount(partition, withCustodian: true);
                        using var tenant = new LedgerRulesTenant(partition);
                        var factory = new TestSessionFactory();
                        var engine = new TenantRulesEngine(tenant, factory, new RulesEngineOptions { MinimumInterval = TimeSpan.Zero, RetryDelay = TimeSpan.FromMilliseconds(200) });
                        var refusals = 0;
                        engine.CycleCompleted += (sender, eventArgs) => { if (eventArgs.Result.RejectedActions != null) { Interlocked.Increment(ref refusals); } };
                        TestSession.RefuseEveryCycle = transaction =>
                        {
                            var node = transaction.Partition.FindNode(accountId)!;
                            transaction.UpdateNode(new Node { NodeId = node.NodeId, DisplayName = "B", FullName = "B", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account, RowVersion = node.RowVersion - 1 });
                            return true;
                        };
                        try
                        {
                            using var stop = new CancellationTokenSource();
                            // On a thread of its own: without backing off, the engine would never yield to its caller.
                            var running = Task.Run(() => engine.RunAsync(stop.Token));
                            Thread.Sleep(1000);
                            stop.Cancel();
                            Check.That(running.Wait(2000), "cancelling stops the engine, even while it backs off");

                            // Without backing off, a zero interval would rebuild hundreds of times a second. With it: at once, then
                            // after 200, 400 and 800 milliseconds, so about four cycles in the second.
                            Check.That(refusals >= 2 && refusals <= 6, $"repeated refusals back off ({refusals} cycles in one second)");
                            Check.That(factory.Sessions.Count == refusals, "each refusal rebuilds the session");
                        }
                        finally
                        {
                            TestSession.RefuseEveryCycle = null;
                        }
                    }

                    public static void AFailedCycleRebuildsFromASnapshot()
                    {
                        var partition = NewPartition();
                        AddAccount(partition, withCustodian: false);
                        using var tenant = new LedgerRulesTenant(partition);
                        var factory = new TestSessionFactory();
                        var engine = new TenantRulesEngine(tenant, factory);
                        TestSession.FailNextFire = true;
                        Check.Throws<InvalidOperationException>(() => engine.RunCycle(), "the failure surfaces");
                        Check.That(tenant.Actions.Count == 0 && partition.Alerts.Count == 0, "the failed cycle's decisions are discarded");
                        var recovered = engine.RunCycle();
                        Check.That(recovered.IsRebuild && factory.Sessions.Count == 2 && partition.Alerts.Count == 1, "the next cycle starts over with a new session");
                    }

                    public static void FallingBehindTheJournalRebuilds()
                    {
                        var partition = NewPartition();
                        using var tenant = new LedgerRulesTenant(partition);
                        var engine = new TenantRulesEngine(tenant, new TestSessionFactory());
                        engine.RunCycle();
                        AddAccount(partition, withCustodian: true);
                        partition.TrimJournal(partition.CommittedSequence);
                        var cycle = engine.RunCycle();
                        Check.That(cycle.IsRebuild && cycle.ThroughSequence == partition.CommittedSequence, "a trimmed journal sends the engine back to a snapshot");
                    }

                    public static void TheEngineRunsOnCommitsAndPrices()
                    {
                        var partition = NewPartition();
                        using var tenant = new LedgerRulesTenant(partition);
                        var factory = new TestSessionFactory();
                        var engine = new TenantRulesEngine(tenant, factory, new RulesEngineOptions { MinimumInterval = TimeSpan.Zero });
                        var cycles = new System.Collections.Concurrent.BlockingCollection<RulesCycleResult>();
                        engine.CycleCompleted += (sender, eventArgs) => cycles.Add(eventArgs.Result);
                        using var stop = new CancellationTokenSource();
                        var running = engine.RunAsync(stop.Token);
                        Check.That(cycles.TryTake(out var first, 2000) && first.IsRebuild, "the engine starts with a snapshot");
                        Check.That(!cycles.TryTake(out _, 100), "and then waits");
                        AddAccount(partition, withCustodian: false);
                        Check.That(cycles.TryTake(out _, 2000) && SpinWait.SpinUntil(() => partition.Alerts.Count == 1, 2000), "a commit runs a cycle, whose decision is committed");
                        Check.That(SpinWait.SpinUntil(() => engine.AppliedSequence == partition.CommittedSequence, 2000), "the engine settles after its own decision comes back");
                        var settled = engine.CycleCount;
                        partition.PublishPrice(new Price { Scheme = "Custodian", Code = "1", Last = 1m, AsOf = DateTimeOffset.UtcNow });
                        Check.That(SpinWait.SpinUntil(() => engine.CycleCount > settled, 2000), "a price runs a cycle too");
                        Check.That(SpinWait.SpinUntil(() => factory.Sessions.Single().Applied.Any(change => change.Fact is PriceFact), 2000), "and the price reaches working memory");
                        stop.Cancel();
                        Check.That(running.Wait(2000), "cancelling stops the engine");
                    }

                    public static void TheHostRunsATenantPerEngine()
                    {
                        var first = NewPartition();
                        var second = NewPartition();
                        AddAccount(first, withCustodian: false);
                        AddAccount(second, withCustodian: false);
                        var host = new RulesEngineHost(new TestSessionFactory(), new RulesEngineOptions { MinimumInterval = TimeSpan.Zero }, maximumConcurrentCycles: 1);
                        var tenant = new LedgerRulesTenant(first);
                        Check.That(host.Start(tenant) && host.Start(new LedgerRulesTenant(second)), "each tenant gets an engine");
                        Check.That(!host.Start(new LedgerRulesTenant(first)), "but only one");
                        Check.That(SpinWait.SpinUntil(() => first.Alerts.Count == 1 && second.Alerts.Count == 1, 2000), "both tenants' rules run");
                        Check.That(host.StopAsync(first.TenantId).Wait(2000) && host.Tenants.Count == 1, "an engine stops");
                        Check.That(host.DisposeAsync().AsTask().Wait(2000) && host.Tenants.Count == 0, "and the host stops the rest");
                    }
                }
            }
            """;

        /// <summary>
        /// The first cycle starts from a snapshot and commits the rules' decisions; the next sees them as facts.
        /// </summary>
        [Fact]
        public void TheFirstCycleLoadsASnapshotAndCommitsTheDecisions() => Run(nameof(this.TheFirstCycleLoadsASnapshotAndCommitsTheDecisions));

        /// <summary>
        /// Committed changes reach the rules in the next cycle.
        /// </summary>
        [Fact]
        public void ChangesReachTheRulesInOrder() => Run(nameof(this.ChangesReachTheRulesInOrder));

        /// <summary>
        /// A burst of latest values is one fact change, at the latest value; removals are retractions.
        /// </summary>
        [Fact]
        public void LatestValuesAreConflated() => Run(nameof(this.LatestValuesAreConflated));

        /// <summary>
        /// When a decision is refused, nothing the cycle decided is committed, and the next cycle decides again.
        /// </summary>
        [Fact]
        public void RefusedDecisionsCommitNothing() => Run(nameof(this.RefusedDecisionsCommitNothing));

        /// <summary>
        /// A decision refused in every cycle slows the engine down instead of rebuilding working memory at the cycle rate.
        /// </summary>
        [Fact]
        public void RepeatedRefusalsBackOff() => Run(nameof(this.RepeatedRefusalsBackOff));

        /// <summary>
        /// A failed cycle discards its decisions and rebuilds from a snapshot.
        /// </summary>
        [Fact]
        public void AFailedCycleRebuildsFromASnapshot() => Run(nameof(this.AFailedCycleRebuildsFromASnapshot));

        /// <summary>
        /// An engine behind a trimmed journal rebuilds from a snapshot.
        /// </summary>
        [Fact]
        public void FallingBehindTheJournalRebuilds() => Run(nameof(this.FallingBehindTheJournalRebuilds));

        /// <summary>
        /// The engine sleeps until a commit or a price wakes it, and stops when cancelled.
        /// </summary>
        [Fact]
        public void TheEngineRunsOnCommitsAndPrices() => Run(nameof(this.TheEngineRunsOnCommitsAndPrices));

        /// <summary>
        /// The host runs one engine per tenant and stops them.
        /// </summary>
        [Fact]
        public void TheHostRunsATenantPerEngine() => Run(nameof(this.TheHostRunsATenantPerEngine));

        private static void Run(string method) => TransactionTests.Run(Server, "Engine", method);
    }
}
