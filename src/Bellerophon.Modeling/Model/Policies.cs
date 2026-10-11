// <copyright file="Policies.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling.Model
{
    /// <summary>
    /// Whether a table survives a restart.
    /// </summary>
    public enum Persistence
    {
        /// <summary>
        /// Sequenced, recorded in history, persisted behind the lock, and exposed through the API.
        /// </summary>
        Durable,

        /// <summary>
        /// Held in memory only; rebuilt after a restart.
        /// </summary>
        Ephemeral,
    }

    /// <summary>
    /// Which changes a table accepts.
    /// </summary>
    public enum WriteMode
    {
        /// <summary>
        /// Insert, update, and delete.
        /// </summary>
        Mutable,

        /// <summary>
        /// Insert only (order intents, fills, violations).
        /// </summary>
        AppendOnly,
    }

    /// <summary>
    /// How concurrent access to a table is managed.
    /// </summary>
    public enum ConcurrencyMode
    {
        /// <summary>
        /// Protected by the tenant's lock.
        /// </summary>
        Locked,

        /// <summary>
        /// A lock-free store of immutable records replaced by atomic swap.  Requires <see cref="Persistence.Ephemeral"/>.
        /// </summary>
        LatestValue,
    }

    /// <summary>
    /// What happens to children when their parent is deleted.
    /// </summary>
    public enum DeleteRule
    {
        /// <summary>
        /// A parent with children cannot be deleted.
        /// </summary>
        Block,

        /// <summary>
        /// Children are deleted with the parent.
        /// </summary>
        Cascade,
    }
}
