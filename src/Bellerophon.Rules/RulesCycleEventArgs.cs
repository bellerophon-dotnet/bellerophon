// <copyright file="RulesCycleEventArgs.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Rules
{
    using System;

    /// <summary>
    /// Reports a completed cycle.
    /// </summary>
    public sealed class RulesCycleEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RulesCycleEventArgs"/> class.
        /// </summary>
        /// <param name="result">What the cycle did.</param>
        public RulesCycleEventArgs(RulesCycleResult result)
        {
            this.Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        /// <summary>
        /// Gets what the cycle did.
        /// </summary>
        public RulesCycleResult Result { get; }
    }

    /// <summary>
    /// Reports a failed cycle: the tenant's working memory is rebuilt from a snapshot before the next one.
    /// </summary>
    public sealed class RulesCycleFailedEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RulesCycleFailedEventArgs"/> class.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="exception">Why the cycle failed.</param>
        public RulesCycleFailedEventArgs(Guid tenantId, Exception exception)
        {
            this.TenantId = tenantId;
            this.Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        }

        /// <summary>
        /// Gets the tenant.
        /// </summary>
        public Guid TenantId { get; }

        /// <summary>
        /// Gets why the cycle failed.
        /// </summary>
        public Exception Exception { get; }
    }
}
