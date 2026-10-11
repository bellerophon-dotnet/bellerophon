// <copyright file="IRulesSession.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Rules
{
    using System.Collections.Generic;

    /// <summary>
    /// One tenant's working memory in a rules engine.  An adapter connects a real engine, such as NRules.  The engine host
    /// uses a session from one thread at a time.
    /// </summary>
    public interface IRulesSession
    {
        /// <summary>
        /// Applies changes to working memory: each fact is inserted, or updates the fact equal to it, or is retracted.
        /// </summary>
        /// <param name="changes">The changes, in order.</param>
        void Apply(IReadOnlyList<FactChange> changes);

        /// <summary>
        /// Runs the rules to completion.  Rules record their decisions in the tenant's rule actions, which the host commits
        /// afterward in one transaction.
        /// </summary>
        /// <returns>The number of rules fired.</returns>
        int Fire();
    }

    /// <summary>
    /// Creates rules sessions for tenants.
    /// </summary>
    public interface IRulesSessionFactory
    {
        /// <summary>
        /// Creates an empty session for a tenant.  The session resolves the services its rules depend on, such as the
        /// tenant's rule actions, through <see cref="System.IServiceProvider.GetService(System.Type)"/> on the tenant.
        /// </summary>
        /// <param name="tenant">The tenant.</param>
        /// <returns>The session.</returns>
        IRulesSession CreateSession(IRulesTenant tenant);
    }
}
