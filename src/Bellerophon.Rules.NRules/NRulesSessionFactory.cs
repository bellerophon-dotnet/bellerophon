// <copyright file="NRulesSessionFactory.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Rules.NRules
{
    using System;
    using global::NRules;
    using global::NRules.Extensibility;

    /// <summary>
    /// Creates an NRules session for each tenant from one compiled rule set.
    /// </summary>
    /// <remarks>
    /// Compile the rules once, for example <c>new NRulesSessionFactory(new ComplianceRuleRepository(activator).Compile())</c>.
    /// Each tenant's session resolves rule dependencies from the tenant first (its rule actions, its partition), then from
    /// the factory's own resolver, so rules declared with <c>Dependency().Resolve(() => actions)</c> get the right tenant's.
    /// </remarks>
    public sealed class NRulesSessionFactory : IRulesSessionFactory
    {
        private readonly ISessionFactory sessionFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="NRulesSessionFactory"/> class.
        /// </summary>
        /// <param name="sessionFactory">The compiled rules.</param>
        public NRulesSessionFactory(ISessionFactory sessionFactory)
        {
            this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        }

        /// <inheritdoc/>
        public IRulesSession CreateSession(IRulesTenant tenant)
        {
            ArgumentNullException.ThrowIfNull(tenant);
            var session = this.sessionFactory.CreateSession();
            session.DependencyResolver = new TenantDependencyResolver(tenant, this.sessionFactory.DependencyResolver);
            return new NRulesSession(session);
        }

        /// <summary>
        /// Resolves rule dependencies from the tenant, then from the shared resolver.
        /// </summary>
        private sealed class TenantDependencyResolver : IDependencyResolver
        {
            private readonly IRulesTenant tenant;

            private readonly IDependencyResolver? fallback;

            public TenantDependencyResolver(IRulesTenant tenant, IDependencyResolver? fallback)
            {
                this.tenant = tenant;
                this.fallback = fallback;
            }

            public object Resolve(IResolutionContext context, Type serviceType) =>
                this.tenant.GetService(serviceType)
                ?? this.fallback?.Resolve(context, serviceType)
                ?? throw new InvalidOperationException($"No service of type {serviceType} is available to the rules of tenant {this.tenant.TenantId}.");
        }
    }
}
