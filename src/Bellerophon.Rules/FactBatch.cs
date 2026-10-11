// <copyright file="FactBatch.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Rules
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The changes to apply to a tenant's working memory in one cycle, through one sequence.
    /// </summary>
    public sealed class FactBatch
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FactBatch"/> class.
        /// </summary>
        /// <param name="throughSequence">The sequence the changes run through.</param>
        /// <param name="isComplete">Whether the changes are complete; if not, the engine rebuilds from a snapshot.</param>
        /// <param name="changes">The changes, in the order to apply them.</param>
        public FactBatch(long throughSequence, bool isComplete, IReadOnlyList<FactChange> changes)
        {
            this.ThroughSequence = throughSequence;
            this.IsComplete = isComplete;
            this.Changes = changes ?? throw new ArgumentNullException(nameof(changes));
        }

        /// <summary>
        /// Gets the sequence the changes run through: the engine's new position once they are applied.
        /// </summary>
        public long ThroughSequence { get; }

        /// <summary>
        /// Gets a value indicating whether the changes are complete. An incomplete batch means the engine fell behind the
        /// tenant's journal and must rebuild its working memory from a snapshot.
        /// </summary>
        public bool IsComplete { get; }

        /// <summary>
        /// Gets the changes, in the order to apply them: inserts and updates parents first, then retractions children first.
        /// </summary>
        public IReadOnlyList<FactChange> Changes { get; }

        /// <summary>
        /// Creates an incomplete batch, telling the engine to rebuild from a snapshot.
        /// </summary>
        /// <param name="sequence">The engine's current position.</param>
        /// <returns>The batch.</returns>
        public static FactBatch Incomplete(long sequence) => new FactBatch(sequence, false, Array.Empty<FactChange>());
    }
}
