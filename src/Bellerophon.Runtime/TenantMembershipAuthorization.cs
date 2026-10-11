// <copyright file="TenantMembershipAuthorization.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Runtime
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Authorization.Policy;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Requires that a caller acting for a person belongs to the tenant named in the route.  Every generated tenant policy
    /// includes it.  Allowed tools acting as themselves (see <see cref="ToolAccessOptions"/>) are authorized by their
    /// application permissions alone.
    /// </summary>
    public sealed class TenantMembershipRequirement : IAuthorizationRequirement
    {
        /// <summary>
        /// The name of the route value that carries the tenant.
        /// </summary>
        public const string TenantRouteValue = "tenantId";

        private TenantMembershipRequirement()
        {
        }

        /// <summary>
        /// Gets the single instance; the requirement has no settings.
        /// </summary>
        public static TenantMembershipRequirement Instance { get; } = new TenantMembershipRequirement();

        /// <summary>
        /// Determines whether an authorization failure was a failed membership check, which is answered with 404 rather
        /// than 403.  Used by code that authorizes explicitly, such as the generated batch controllers.
        /// </summary>
        /// <param name="failure">The failure, if any.</param>
        /// <returns><c>true</c> if the caller is not a member of the tenant.</returns>
        public static bool IsMembershipFailure(AuthorizationFailure? failure) =>
            failure?.FailedRequirements.OfType<TenantMembershipRequirement>().Any() == true;
    }

    /// <summary>
    /// Evaluates <see cref="TenantMembershipRequirement"/>: one lookup of the caller's issuer and subject in the tenant's
    /// membership, unless the caller is an allowed tool (see <see cref="ToolAccessOptions"/>).
    /// </summary>
    public sealed partial class TenantMembershipHandler : AuthorizationHandler<TenantMembershipRequirement>
    {
        private readonly ITenantMembership membership;

        private readonly ToolAccessOptions tools;

        private readonly ILogger<TenantMembershipHandler> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TenantMembershipHandler"/> class.
        /// </summary>
        /// <param name="membership">The membership the caller is checked against.</param>
        /// <param name="tools">The applications allowed to act as tools.</param>
        /// <param name="logger">Records every tool access, and every refused application.</param>
        public TenantMembershipHandler(ITenantMembership membership, IOptions<ToolAccessOptions> tools, ILogger<TenantMembershipHandler> logger)
        {
            this.membership = membership;
            this.tools = tools?.Value ?? throw new ArgumentNullException(nameof(tools));
            this.logger = logger;
        }

        /// <inheritdoc/>
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantMembershipRequirement requirement)
        {
            if (IdentityClaims.TryGetApplication(context.User, out var clientId))
            {
                // An application acting as itself: it may skip membership only if it is an allowed tool.  Every such access
                // is logged, since a tool can reach any tenant.
                TryGetTenant(context.Resource, out var accessed);
                if (this.tools.Allows(clientId))
                {
                    this.LogToolAccess(clientId, accessed);
                    context.Succeed(requirement);
                }
                else
                {
                    this.LogApplicationRefused(clientId, accessed);
                }

                return Task.CompletedTask;
            }

            if (TryGetTenant(context.Resource, out var tenantId) &&
                IdentityClaims.TryGetIdentity(context.User, out var issuer, out var subject) &&
                this.membership.IsMember(tenantId, issuer, subject))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Tool {ClientId} accessed tenant {TenantId} as itself.")]
        private partial void LogToolAccess(string clientId, Guid tenantId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Application {ClientId} is not an allowed tool; refused access to tenant {TenantId}. Add its client ID to Tools:ClientIds if it should be one.")]
        private partial void LogApplicationRefused(string clientId, Guid tenantId);

        /// <summary>
        /// Finds the tenant: from the route when authorizing a request, or passed directly when authorizing in code.
        /// </summary>
        private static bool TryGetTenant(object? resource, out Guid tenantId)
        {
            switch (resource)
            {
                case Guid tenant:
                    tenantId = tenant;
                    return true;
                case HttpContext httpContext:
                    return Guid.TryParse(httpContext.Request.RouteValues[TenantMembershipRequirement.TenantRouteValue]?.ToString(), out tenantId);
                default:
                    tenantId = Guid.Empty;
                    return false;
            }
        }
    }

    /// <summary>
    /// Answers a failed membership check with 404 rather than 403, so a caller cannot tell a tenant it does not belong to
    /// from one that does not exist.  Membership is checked before scope: a non-member always sees 404, and only a member
    /// lacking a scope sees 403.  Unauthenticated callers still see 401.
    /// </summary>
    public sealed class MembershipResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler fallback = new AuthorizationMiddlewareResultHandler();

        /// <inheritdoc/>
        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (authorizeResult is not null &&
                authorizeResult.Forbidden &&
                authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<TenantMembershipRequirement>().Any() == true)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return this.fallback.HandleAsync(next, context, policy, authorizeResult!);
        }
    }
}
