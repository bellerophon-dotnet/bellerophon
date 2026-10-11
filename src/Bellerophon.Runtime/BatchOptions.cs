// <copyright file="BatchOptions.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Runtime
{
    /// <summary>
    /// Limits on the batch endpoint.  A batch holds its tenant's lock while it applies, so its size bounds how long every
    /// other reader and writer of that tenant, including the rules engine, can be kept waiting.
    /// </summary>
    public sealed class BatchOptions
    {
        /// <summary>
        /// The configuration section these options are read from.
        /// </summary>
        public const string SectionName = "Batch";

        /// <summary>
        /// The default limit.  Measured at about 0.6 microseconds per operation on a two-core machine (inserts of rows with a
        /// parent, write-behind on), 10,000 operations hold the lock for about 7 milliseconds, well inside the rules engine's
        /// 50-millisecond cycle; undoing a refused batch takes less.
        /// </summary>
        public const int DefaultMaximumOperations = 10_000;

        /// <summary>
        /// Gets or sets the most operations one batch may hold.  A larger batch is refused with 413 and changes nothing; the
        /// problem response names the limit (<c>maximumOperations</c>) so a client can split its work.
        /// </summary>
        public int MaximumOperations { get; set; } = DefaultMaximumOperations;
    }
}
