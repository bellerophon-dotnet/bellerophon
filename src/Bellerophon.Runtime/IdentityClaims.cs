// <copyright file="IdentityClaims.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Runtime
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Security.Claims;

    /// <summary>
    /// Reads the caller's identity, issuer and subject, from a validated token.  Both are signed by the identity provider,
    /// so neither can be forged; together they are what the built-in User table binds to.
    /// </summary>
    public static class IdentityClaims
    {
        /// <summary>
        /// The issuer claim, as issued.
        /// </summary>
        public const string IssuerClaim = "iss";

        /// <summary>
        /// Microsoft's object-identifier claim, as issued: the same for a person across every application.
        /// </summary>
        public const string ObjectIdentifierClaim = "oid";

        /// <summary>
        /// Microsoft's object-identifier claim after the JWT bearer handler maps it.
        /// </summary>
        public const string MappedObjectIdentifierClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

        /// <summary>
        /// The standard subject claim, as issued.
        /// </summary>
        public const string SubjectClaim = "sub";

        /// <summary>
        /// The identity-type claim, as issued (an optional claim in Microsoft Entra): <c>app</c> for an application acting as
        /// itself, <c>user</c> for a person.
        /// </summary>
        public const string IdentityTypeClaim = "idtyp";

        /// <summary>
        /// The identity type of an application acting as itself.
        /// </summary>
        public const string ApplicationIdentityType = "app";

        /// <summary>
        /// The authorized-party claim: the client ID of the application the token was issued to (version 2.0 tokens).
        /// </summary>
        public const string AuthorizedPartyClaim = "azp";

        /// <summary>
        /// The application ID claim: the client ID of the application the token was issued to (version 1.0 tokens).
        /// </summary>
        public const string ApplicationIdClaim = "appid";

        /// <summary>
        /// Gets the client ID of an application acting as itself.  Only a token that says so qualifies: its identity type is
        /// <c>app</c>, it carries no delegated scope, and it names the application.  Anything else, including a token that
        /// merely lacks scopes, is not an application's: fail closed.
        /// </summary>
        /// <param name="user">The caller.</param>
        /// <param name="clientId">The application's client ID.</param>
        /// <returns><c>true</c> if the caller is an application acting as itself.</returns>
        public static bool TryGetApplication(ClaimsPrincipal user, [NotNullWhen(true)] out string? clientId)
        {
            if (user is null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            clientId = null;
            if (ScopeClaims.IsDelegated(user) ||
                !string.Equals(user.FindFirst(IdentityTypeClaim)?.Value, ApplicationIdentityType, StringComparison.Ordinal))
            {
                return false;
            }

            var named = user.FindFirst(AuthorizedPartyClaim)?.Value ?? user.FindFirst(ApplicationIdClaim)?.Value;
            if (string.IsNullOrEmpty(named))
            {
                return false;
            }

            clientId = named;
            return true;
        }

        /// <summary>
        /// Gets the caller's issuer and subject.
        /// </summary>
        /// <param name="user">The caller.</param>
        /// <param name="issuer">The identity provider that issued the token.</param>
        /// <param name="subject">
        /// The identity's subject: Microsoft's object identifier when present (Microsoft's <c>sub</c> differs per
        /// application, so it is not used), otherwise the standard subject.
        /// </param>
        /// <returns><c>true</c> if both were found.</returns>
        public static bool TryGetIdentity(ClaimsPrincipal user, [NotNullWhen(true)] out string? issuer, [NotNullWhen(true)] out string? subject)
        {
            if (user is null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            var subjectClaim = user.FindFirst(ObjectIdentifierClaim)
                ?? user.FindFirst(MappedObjectIdentifierClaim)
                ?? user.FindFirst(SubjectClaim)
                ?? user.FindFirst(ClaimTypes.NameIdentifier);
            subject = subjectClaim?.Value;
            issuer = user.FindFirst(IssuerClaim)?.Value ?? subjectClaim?.Issuer;
            if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject))
            {
                issuer = null;
                subject = null;
                return false;
            }

            return true;
        }
    }
}
