// <copyright file="NRulesSession.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Rules.NRules
{
    using System;
    using System.Collections.Generic;
    using global::NRules;

    /// <summary>
    /// One tenant's NRules session.
    /// </summary>
    /// <remarks>
    /// NRules finds a fact in working memory by equality.  Bellerophon's facts are immutable and equal by key, so an update
    /// replaces the fact for the same row, and a retraction removes it, whatever the values.
    /// </remarks>
    public sealed class NRulesSession : IRulesSession
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NRulesSession"/> class.
        /// </summary>
        /// <param name="session">The NRules session.</param>
        public NRulesSession(ISession session)
        {
            this.Session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>
        /// Gets the NRules session, for queries and diagnostics.  Use it only from the engine's cycle.
        /// </summary>
        public ISession Session { get; }

        /// <inheritdoc/>
        public void Apply(IReadOnlyList<FactChange> changes)
        {
            ArgumentNullException.ThrowIfNull(changes);
            foreach (var change in changes)
            {
                if (change.IsRetraction)
                {
                    // A row may be deleted before its fact was ever inserted; that is not an error.
                    this.Session.TryRetract(change.Fact);
                }
                else if (!this.Session.TryUpdate(change.Fact))
                {
                    this.Session.Insert(change.Fact);
                }
            }
        }

        /// <inheritdoc/>
        public int Fire() => this.Session.Fire();
    }
}
