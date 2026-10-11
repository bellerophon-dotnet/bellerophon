// <copyright file="NRulesTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System;
    using System.Reflection;
    using Xunit;

    /// <summary>
    /// Tests the compliance engine with real NRules rules: the adapter, the generated facts in NRules' working memory, the
    /// tenant's rule actions resolved as a rule dependency, and decisions that settle.  Each scenario is C# compiled together
    /// with the generated code.
    /// </summary>
    public class NRulesTests
    {
        private static readonly Lazy<Assembly> Server = new Lazy<Assembly>(() => TransactionTests.Compile(Fixture.Read("bellerophon.json"), "Primary", Scenarios, withRules: true));

        private const string Scenarios = TransactionTests.Common + """
            namespace Scenarios
            {
                using AtlasLedger.Model;
                using AtlasLedger.Model.Primary;
                using Bellerophon.Rules;
                using Bellerophon.Rules.NRules;
                using NRules;
                using NRules.Fluent;
                using NRules.Fluent.Dsl;

                public static class Decisions
                {
                    public static void Raise(LedgerRuleActions actions, Guid nodeId, string rule) =>
                        actions.Add(transaction => transaction.InsertAlert(new Alert { AlertId = Guid.NewGuid(), NodeId = nodeId, Rule = rule, Message = rule }));

                    public static void Clear(LedgerRuleActions actions, AlertFact alert)
                    {
                        var (alertId, rowVersion) = (alert.AlertId, alert.RowVersion);
                        actions.Add(transaction => transaction.DeleteAlert(alertId, rowVersion));
                    }
                }

                public sealed class MissingCustodianRaise : Rule
                {
                    public override void Define()
                    {
                        AccountFact account = default!;
                        LedgerRuleActions actions = default!;
                        Dependency().Resolve(() => actions);
                        When()
                            .Match(() => account)
                            .Not<SymbolFact>(symbol => symbol.NodeId == account.AccountId, symbol => symbol.Scheme == "Custodian")
                            .Not<AlertFact>(alert => alert.NodeId == account.AccountId, alert => alert.Rule == "MissingCustodian");
                        Then().Do(_ => Decisions.Raise(actions, account.AccountId, "MissingCustodian"));
                    }
                }

                public sealed class MissingCustodianClear : Rule
                {
                    public override void Define()
                    {
                        AccountFact account = default!;
                        AlertFact alert = default!;
                        LedgerRuleActions actions = default!;
                        Dependency().Resolve(() => actions);
                        When()
                            .Match(() => account)
                            .Exists<SymbolFact>(symbol => symbol.NodeId == account.AccountId, symbol => symbol.Scheme == "Custodian")
                            .Match(() => alert, candidate => candidate.NodeId == account.AccountId, candidate => candidate.Rule == "MissingCustodian");
                        Then().Do(_ => Decisions.Clear(actions, alert));
                    }
                }

                public sealed class InvalidPriceRaise : Rule
                {
                    public override void Define()
                    {
                        SymbolFact symbol = default!;
                        PriceFact price = default!;
                        LedgerRuleActions actions = default!;
                        Dependency().Resolve(() => actions);
                        When()
                            .Match(() => symbol)
                            .Match(() => price, candidate => candidate.Scheme == symbol.Scheme, candidate => candidate.Code == symbol.Code, candidate => candidate.Last <= 0m)
                            .Not<AlertFact>(alert => alert.NodeId == symbol.NodeId, alert => alert.Rule == "InvalidPrice");
                        Then().Do(_ => Decisions.Raise(actions, symbol.NodeId, "InvalidPrice"));
                    }
                }

                public sealed class InvalidPriceClear : Rule
                {
                    public override void Define()
                    {
                        SymbolFact symbol = default!;
                        PriceFact price = default!;
                        AlertFact alert = default!;
                        LedgerRuleActions actions = default!;
                        Dependency().Resolve(() => actions);
                        When()
                            .Match(() => symbol)
                            .Match(() => price, candidate => candidate.Scheme == symbol.Scheme, candidate => candidate.Code == symbol.Code, candidate => candidate.Last > 0m)
                            .Match(() => alert, candidate => candidate.NodeId == symbol.NodeId, candidate => candidate.Rule == "InvalidPrice");
                        Then().Do(_ => Decisions.Clear(actions, alert));
                    }
                }

                // Keeps the session it created, so a scenario can look into working memory.
                public sealed class RecordingSessionFactory : IRulesSessionFactory
                {
                    private readonly NRulesSessionFactory inner;

                    public RecordingSessionFactory(NRulesSessionFactory inner) => this.inner = inner;

                    public NRulesSession? Session { get; private set; }

                    public IRulesSession CreateSession(IRulesTenant tenant) => this.Session = (NRulesSession)this.inner.CreateSession(tenant);
                }

                public static class Engine
                {
                    private static readonly Lazy<ISessionFactory> Rules = new Lazy<ISessionFactory>(() =>
                    {
                        var repository = new RuleRepository();
                        repository.Load(spec => spec.From(typeof(Decisions).Assembly));
                        return repository.Compile();
                    });

                    private static LedgerPartition NewPartition() => new Ledger().GetPartition(Guid.NewGuid());

                    private static Guid AddAccount(LedgerPartition partition, string? custodian)
                    {
                        var node = partition.InsertNode(new Node { NodeId = Guid.NewGuid(), DisplayName = "A", FullName = "A", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account });
                        partition.InsertAccount(new Account { AccountId = node.NodeId, AccountType = AccountType.ManagedAccount });
                        if (custodian != null)
                        {
                            partition.InsertSymbol(new Symbol { Scheme = "Custodian", Code = custodian, NodeId = node.NodeId });
                        }

                        return node.NodeId;
                    }

                    private static TenantRulesEngine NewEngine(LedgerRulesTenant tenant) => new TenantRulesEngine(tenant, new NRulesSessionFactory(Rules.Value));

                    public static void FactsAreReplacedAndRetractedByKey()
                    {
                        using var tenant = new LedgerRulesTenant(NewPartition());
                        var session = (NRulesSession)new NRulesSessionFactory(Rules.Value).CreateSession(tenant);
                        session.Apply(new[] { new FactChange(new PriceFact(new Price { Scheme = "X", Code = "1", Last = 1m }), false) });
                        session.Apply(new[] { new FactChange(new PriceFact(new Price { Scheme = "X", Code = "1", Last = 2m }), false) });
                        var prices = session.Session.Query<PriceFact>().ToList();
                        Check.That(prices.Count == 1 && prices[0].Last == 2m, "a newer fact for the same key replaces the older one in working memory");
                        session.Apply(new[] { new FactChange(new PriceFact(new Price { Scheme = "X", Code = "1", Last = 99m }), true) });
                        Check.That(!session.Session.Query<PriceFact>().Any(), "a retraction removes the fact by key, whatever its values");
                        session.Apply(new[] { new FactChange(new PriceFact(new Price { Scheme = "X", Code = "1", Last = 1m }), true) });
                        Check.That(!session.Session.Query<PriceFact>().Any(), "retracting a fact that isn't there is harmless");
                    }

                    public static void TheRulesRaiseAndClearAlerts()
                    {
                        var partition = NewPartition();
                        var accountId = AddAccount(partition, custodian: null);
                        AddAccount(partition, custodian: "1111");
                        using var tenant = new LedgerRulesTenant(partition);
                        var engine = NewEngine(tenant);
                        var first = engine.RunCycle();
                        Check.That(first.IsRebuild && first.RulesFired == 1, "the first cycle raises one alert");
                        Check.That(partition.Alerts.Single().NodeId == accountId, "the alert is in the ledger");
                        var settled = engine.RunCycle();
                        Check.That(settled.RulesFired == 0 && settled.FactChanges == 1, "the alert comes back as a fact and the rule settles");
                        partition.InsertSymbol(new Symbol { Scheme = "Custodian", Code = "2222", NodeId = accountId });
                        var cleared = engine.RunCycle();
                        Check.That(cleared.RulesFired == 1 && !partition.Alerts.Any(), "a custodian number clears the alert");
                        var quiet = engine.RunCycle();
                        Check.That(quiet.RulesFired == 0 && engine.AppliedSequence == partition.CommittedSequence, "the retraction reaches the rules and they settle");
                    }

                    public static void EachTenantResolvesItsOwnActions()
                    {
                        var first = NewPartition();
                        var second = NewPartition();
                        var firstAccount = AddAccount(first, custodian: null);
                        var secondAccount = AddAccount(second, custodian: null);
                        AddAccount(second, custodian: null);
                        using var firstTenant = new LedgerRulesTenant(first);
                        using var secondTenant = new LedgerRulesTenant(second);
                        var factory = new NRulesSessionFactory(Rules.Value);
                        new TenantRulesEngine(firstTenant, factory).RunCycle();
                        new TenantRulesEngine(secondTenant, factory).RunCycle();
                        Check.That(first.Alerts.Count() == 1 && first.Alerts.Single().NodeId == firstAccount, "the first tenant's decisions land in the first tenant");
                        Check.That(second.Alerts.Count() == 2 && second.Alerts.Any(alert => alert.NodeId == secondAccount), "the second tenant's decisions land in the second tenant");
                    }

                    public static void ARefusedCycleLosesNoDecision()
                    {
                        var partition = NewPartition();
                        var settledAccount = AddAccount(partition, custodian: "1111");
                        using var tenant = new LedgerRulesTenant(partition);
                        var engine = NewEngine(tenant);
                        Check.That(engine.RunCycle().RulesFired == 0, "every account has a custodian: nothing to decide");

                        // One cycle in which a rule raises an alert, and another decision in the same cycle is refused, as when an
                        // API write moves a row on between the rules reading it and the decision committing.
                        var accountId = AddAccount(partition, custodian: null);
                        var node = partition.FindNode(settledAccount)!;
                        tenant.Actions.Add(transaction => transaction.UpdateNode(new Node { NodeId = node.NodeId, DisplayName = "B", FullName = "B", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account, RowVersion = node.RowVersion - 1 }));
                        var refused = engine.RunCycle();
                        Check.That(refused.RejectedActions is System.Data.DBConcurrencyException && !partition.Alerts.Any(), "the refusal takes the alert down with it");

                        // Nothing about the account changes, so a Rete session would never fire its rule again. The engine must still decide again.
                        var retry = engine.RunCycle();
                        Check.That(partition.Alerts.Count() == 1 && partition.Alerts.Single().NodeId == accountId, "the alert the refused cycle lost is raised by the next cycle");
                        Check.That(engine.RunCycle().RulesFired == 0, "and the rules settle");
                    }

                    public static void PricesUpdateFactsInPlace()
                    {
                        var partition = NewPartition();
                        var nodeId = AddAccount(partition, custodian: "1234");
                        partition.PublishPrice(new Price { Scheme = "Custodian", Code = "1234", Last = 0m, AsOf = DateTimeOffset.UtcNow });
                        using var tenant = new LedgerRulesTenant(partition);
                        var factory = new RecordingSessionFactory(new NRulesSessionFactory(Rules.Value));
                        var engine = new TenantRulesEngine(tenant, factory);
                        engine.RunCycle();
                        Check.That(partition.Alerts.Single().Rule == "InvalidPrice", "a zero price raises an alert");
                        engine.RunCycle();
                        for (var tick = 1; tick <= 1000; tick++)
                        {
                            partition.PublishPrice(new Price { Scheme = "Custodian", Code = "1234", Last = tick, AsOf = DateTimeOffset.UtcNow });
                        }

                        var burst = engine.RunCycle();
                        Check.That(burst.FactChanges == 1 && burst.RulesFired == 1 && !partition.Alerts.Any(), "1,000 ticks are one fact update, which clears the alert");
                        var prices = factory.Session!.Session.Query<PriceFact>().ToList();
                        Check.That(prices.Count == 1 && prices[0].Last == 1000m, "working memory holds the latest price once");
                        Check.That(engine.RunCycle().RulesFired == 0, "the rules settle");
                    }
                }
            }
            """;

        /// <summary>
        /// A newer fact for the same row replaces the older one in NRules' working memory, and a retraction removes it by key.
        /// </summary>
        [Fact]
        public void FactsAreReplacedAndRetractedByKey() => Run(nameof(this.FactsAreReplacedAndRetractedByKey));

        /// <summary>
        /// NRules rules raise an alert, see it come back as a fact, clear it when the data changes, and settle.
        /// </summary>
        [Fact]
        public void TheRulesRaiseAndClearAlerts() => Run(nameof(this.TheRulesRaiseAndClearAlerts));

        /// <summary>
        /// One compiled rule set serves every tenant, and each tenant's rules record decisions in that tenant.
        /// </summary>
        [Fact]
        public void EachTenantResolvesItsOwnActions() => Run(nameof(this.EachTenantResolvesItsOwnActions));

        /// <summary>
        /// A decision lost because another decision in its cycle was refused is made again, even though none of its facts changed.
        /// </summary>
        [Fact]
        public void ARefusedCycleLosesNoDecision() => Run(nameof(this.ARefusedCycleLosesNoDecision));

        /// <summary>
        /// A burst of prices is one fact update in NRules, and the rules react to the latest value.
        /// </summary>
        [Fact]
        public void PricesUpdateFactsInPlace() => Run(nameof(this.PricesUpdateFactsInPlace));

        private static void Run(string method) => TransactionTests.Run(Server, "Engine", method);
    }
}
