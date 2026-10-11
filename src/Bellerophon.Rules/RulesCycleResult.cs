// <copyright file="RulesCycleResult.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Rules
{
    using System;

    /// <summary>
    /// What one cycle of a tenant's rules engine did.
    /// </summary>
    public sealed class RulesCycleResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RulesCycleResult"/> class.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="throughSequence">The sequence working memory now reflects.</param>
        /// <param name="factChanges">The number of fact changes applied.</param>
        /// <param name="rulesFired">The number of rules fired.</param>
        /// <param name="committedSequence">The sequence of the transaction that committed the rules' decisions, or zero.</param>
        /// <param name="rejectedActions">Why the rules' decisions were refused, or null.</param>
        /// <param name="isRebuild">Whether working memory was rebuilt from a snapshot.</param>
        public RulesCycleResult(Guid tenantId, long throughSequence, int factChanges, int rulesFired, long committedSequence, Exception? rejectedActions, bool isRebuild)
        {
            this.TenantId = tenantId;
            this.ThroughSequence = throughSequence;
            this.FactChanges = factChanges;
            this.RulesFired = rulesFired;
            this.CommittedSequence = committedSequence;
            this.RejectedActions = rejectedActions;
            this.IsRebuild = isRebuild;
        }

        /// <summary>
        /// Gets the tenant.
        /// </summary>
        public Guid TenantId { get; }

        /// <summary>
        /// Gets the sequence working memory reflects after the cycle.
        /// </summary>
        public long ThroughSequence { get; }

        /// <summary>
        /// Gets the number of fact changes applied.
        /// </summary>
        public int FactChanges { get; }

        /// <summary>
        /// Gets the number of rules fired.
        /// </summary>
        public int RulesFired { get; }

        /// <summary>
        /// Gets the sequence of the transaction that committed the rules' decisions, or zero if there were none.
        /// </summary>
        public long CommittedSequence { get; }

        /// <summary>
        /// Gets why the rules' decisions were refused (a stale version or a broken constraint), or null.  Nothing was
        /// committed, including the cycle's other decisions; the next cycle rebuilds working memory from a snapshot, so every
        /// rule decides again against the committed data.  Repeated refusals slow the engine down (see
        /// <see cref="RulesEngineOptions.RetryDelay"/>); a host should alert on them, since they usually mean a rule is wrong.
        /// </summary>
        public Exception? RejectedActions { get; }

        /// <summary>
        /// Gets a value indicating whether working memory was rebuilt from a snapshot: on the first cycle, after the engine
        /// fell behind the journal, after a failed cycle, or after a refused one.
        /// </summary>
        public bool IsRebuild { get; }
    }
}
