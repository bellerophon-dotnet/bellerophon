// <copyright file="TenantMembership.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Runtime
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.Immutable;

    /// <summary>
    /// Answers which tenants an identity belongs to, from the built-in User table's rows.
    /// </summary>
    public interface ITenantMembership
    {
        /// <summary>
        /// Determines whether an identity has a user in a tenant.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="issuer">The identity's issuer.</param>
        /// <param name="subject">The identity's subject.</param>
        /// <returns><c>true</c> if the identity is a member of the tenant.</returns>
        bool IsMember(Guid tenantId, string issuer, string subject);

        /// <summary>
        /// Lists the tenants an identity belongs to, for the tenant picker.
        /// </summary>
        /// <param name="issuer">The identity's issuer.</param>
        /// <param name="subject">The identity's subject.</param>
        /// <returns>The tenants, in no particular order.</returns>
        IReadOnlyList<Guid> TenantsOf(string issuer, string subject);
    }

    /// <summary>
    /// A thread-safe, in-memory membership index keyed by issuer and subject.
    /// </summary>
    /// <remarks>
    /// Each identity maps to an immutable array of tenants, replaced whole on every change, so readers never see a
    /// partly updated set.  Until the store layer exists, the host maintains it; in a later phase the store updates it in
    /// the same transaction as the User row, so the index can never disagree with the data.
    /// </remarks>
    public sealed class MembershipIndex : ITenantMembership
    {
        private readonly ConcurrentDictionary<(string Issuer, string Subject), ImmutableArray<Guid>> tenants =
            new ConcurrentDictionary<(string Issuer, string Subject), ImmutableArray<Guid>>();

        /// <summary>
        /// Records that an identity has a user in a tenant.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="issuer">The identity's issuer.</param>
        /// <param name="subject">The identity's subject.</param>
        public void Add(Guid tenantId, string issuer, string subject) =>
            this.tenants.AddOrUpdate(
                (issuer, subject),
                _ => ImmutableArray.Create(tenantId),
                (_, existing) => existing.Contains(tenantId) ? existing : existing.Add(tenantId));

        /// <summary>
        /// Records that an identity no longer has a user in a tenant.  Access ends immediately.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="issuer">The identity's issuer.</param>
        /// <param name="subject">The identity's subject.</param>
        public void Remove(Guid tenantId, string issuer, string subject) =>
            this.tenants.AddOrUpdate(
                (issuer, subject),
                _ => ImmutableArray<Guid>.Empty,
                (_, existing) => existing.Remove(tenantId));

        /// <inheritdoc/>
        public bool IsMember(Guid tenantId, string issuer, string subject) =>
            this.tenants.TryGetValue((issuer, subject), out var memberships) && memberships.Contains(tenantId);

        /// <inheritdoc/>
        public IReadOnlyList<Guid> TenantsOf(string issuer, string subject) =>
            this.tenants.TryGetValue((issuer, subject), out var memberships) ? memberships : ImmutableArray<Guid>.Empty;
    }
}
