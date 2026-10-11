// <copyright file="BellerophonServiceCollectionExtensions.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Runtime
{
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Authorization.Policy;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;

    /// <summary>
    /// Registers the runtime's services.
    /// </summary>
    public static class BellerophonServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the tenant membership check and its 404 result handling.  The host also registers an
        /// <see cref="ITenantMembership"/>, such as a <see cref="MembershipIndex"/>.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <returns>The same services, for chaining.</returns>
        public static IServiceCollection AddBellerophonAuthorization(this IServiceCollection services)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, TenantMembershipHandler>());
            services.AddSingleton<IAuthorizationMiddlewareResultHandler, MembershipResultHandler>();
            return services;
        }
    }
}
