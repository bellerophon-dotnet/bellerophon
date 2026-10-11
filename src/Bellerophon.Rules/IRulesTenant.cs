// <copyright file="IRulesTenant.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Rules
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A tenant as the rules engine sees it: where facts come from and where the rules' decisions go.  Bellerophon generates
    /// one for each model ({Model}RulesTenant) in any project that references this library.
    /// </summary>
    /// <remarks>
    /// It also serves the services rules depend on, through <see cref="IServiceProvider.GetService(Type)"/>: at least the
    /// tenant's rule actions, the buffer in which rules record their decisions.
    /// </remarks>
    public interface IRulesTenant : IServiceProvider
    {
        /// <summary>
        /// Gets the tenant.
        /// </summary>
        Guid TenantId { get; }

        /// <summary>
        /// Takes a snapshot of every fact: every row as of the last commit, and every latest value.
        /// </summary>
        /// <returns>The facts, through the last commit.</returns>
        FactBatch TakeSnapshot();

        /// <summary>
        /// Reads every change after a sequence: committed rows, coalesced, and the latest values that changed since the last
        /// read, conflated.
        /// </summary>
        /// <param name="sequence">The engine's position.</param>
        /// <returns>The changes, or an incomplete batch if the engine fell behind the tenant's journal.</returns>
        FactBatch ReadChangesSince(long sequence);

        /// <summary>
        /// Waits until there is something new to read: a commit past the sequence, or a latest value that changed.
        /// </summary>
        /// <param name="sequence">The engine's position.</param>
        /// <param name="cancellationToken">Stops waiting.</param>
        /// <returns>A task that completes when there is something to read.</returns>
        Task WaitForChangesAsync(long sequence, CancellationToken cancellationToken);

        /// <summary>
        /// Commits the decisions rules recorded during the cycle, all in one transaction.
        /// </summary>
        /// <returns>The transaction's sequence, or zero if there was nothing to commit.</returns>
        /// <exception cref="System.Data.DBConcurrencyException">A decision was based on a row that has changed since; nothing was committed.</exception>
        /// <exception cref="System.Data.ConstraintException">A decision broke a constraint; nothing was committed.</exception>
        long ApplyActions();

        /// <summary>
        /// Discards decisions recorded by a cycle that failed before they could be committed.
        /// </summary>
        void DiscardActions();
    }
}
