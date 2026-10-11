// <copyright file="ScopeClaims.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Runtime
{
    using System;
    using System.Security.Claims;

    /// <summary>
    /// Reads OAuth scopes from a caller's token.  Every generated access policy uses this check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A caller is authorized when its token carries any accepted name in either of two places:
    /// </para>
    /// <list type="bullet">
    /// <item>a delegated scope, in the space-separated <c>scp</c> claim (Microsoft) or <c>scope</c> claim (the OAuth
    /// standard): an application acting for a signed-in person;</item>
    /// <item>an application permission, in the <c>roles</c> claim: a tool acting as itself (the client-credentials flow).</item>
    /// </list>
    /// <para>
    /// The JWT bearer handler maps claim names by default, so the mapped names are accepted too.  Authorization comes only
    /// from the token, which the identity provider issues from its configuration, never from data in the ledger; that is
    /// what lets an empty system be bootstrapped through its own API.
    /// </para>
    /// </remarks>
    public static class ScopeClaims
    {
        /// <summary>
        /// The delegated-scope claim, as Microsoft's identity platform issues it.
        /// </summary>
        public const string ScopeClaim = "scp";

        /// <summary>
        /// The delegated-scope claim as the OAuth JWT access-token profile (RFC 9068) names it, used by most other providers.
        /// </summary>
        public const string StandardScopeClaim = "scope";

        /// <summary>
        /// The delegated-scope claim after the JWT bearer handler maps it.
        /// </summary>
        public const string MappedScopeClaim = "http://schemas.microsoft.com/identity/claims/scope";

        /// <summary>
        /// The application-permission claim, as issued.
        /// </summary>
        public const string RolesClaim = "roles";

        /// <summary>
        /// Determines whether the caller's token carries any of the accepted scopes or application permissions.
        /// </summary>
        /// <param name="user">The caller.</param>
        /// <param name="accepted">The accepted names.</param>
        /// <returns><c>true</c> if any accepted name is present.</returns>
        public static bool HasAny(ClaimsPrincipal user, params string[] accepted)
        {
            if (user is null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            foreach (var claim in user.Claims)
            {
                if (IsScopeClaim(claim))
                {
                    foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (Array.IndexOf(accepted, scope) >= 0)
                        {
                            return true;
                        }
                    }
                }
                else if ((claim.Type == RolesClaim || claim.Type == ClaimTypes.Role) && Array.IndexOf(accepted, claim.Value) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether the caller is an application acting for a signed-in person (a delegated token).  Delegated
        /// tokens carry scopes.  A token without scopes is not thereby a tool's: see
        /// <see cref="IdentityClaims.TryGetApplication"/>, which requires the token to say so.
        /// </summary>
        /// <param name="user">The caller.</param>
        /// <returns><c>true</c> for a delegated token.</returns>
        public static bool IsDelegated(ClaimsPrincipal user)
        {
            if (user is null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            foreach (var claim in user.Claims)
            {
                if (IsScopeClaim(claim))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsScopeClaim(Claim claim) =>
            claim.Type == ScopeClaim || claim.Type == MappedScopeClaim || claim.Type == StandardScopeClaim;
    }
}
