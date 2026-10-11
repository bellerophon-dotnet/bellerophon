// <copyright file="ScopeClaimsTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System.Security.Claims;
    using Bellerophon.Runtime;
    using Xunit;

    /// <summary>
    /// Tests for the scope check behind every generated access policy.
    /// </summary>
    public class ScopeClaimsTests
    {
        /// <summary>
        /// A delegated scope is found in the space-separated scp claim.
        /// </summary>
        [Fact]
        public void DelegatedScopeIsAccepted()
        {
            var user = Caller(new Claim("scp", "Ledger.Read Ledger.Write"));

            Assert.True(ScopeClaims.HasAny(user, "Ledger.Write"));
        }

        /// <summary>
        /// An application permission is found in a roles claim.
        /// </summary>
        [Fact]
        public void ApplicationPermissionIsAccepted()
        {
            var user = Caller(new Claim("roles", "Users.Write"), new Claim("roles", "Dossier.Write"));

            Assert.True(ScopeClaims.HasAny(user, "Dossier.Write"));
        }

        /// <summary>
        /// The claim names the JWT bearer handler maps to are accepted too.
        /// </summary>
        [Fact]
        public void MappedClaimNamesAreAccepted()
        {
            var delegated = Caller(new Claim(ScopeClaims.MappedScopeClaim, "Ledger.Read"));
            var application = Caller(new Claim(ClaimTypes.Role, "Users.Write"));

            Assert.True(ScopeClaims.HasAny(delegated, "Ledger.Read"));
            Assert.True(ScopeClaims.HasAny(application, "Users.Write"));
        }

        /// <summary>
        /// A caller without any accepted name is refused, and partial matches do not count.
        /// </summary>
        [Fact]
        public void MissingScopeIsRefused()
        {
            var user = Caller(new Claim("scp", "Ledger.Read Ledger.ReadWrite"));

            Assert.False(ScopeClaims.HasAny(user, "Ledger.Write", "Dossier.Read"));
        }

        private static ClaimsPrincipal Caller(params Claim[] claims) =>
            new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
    }
}
