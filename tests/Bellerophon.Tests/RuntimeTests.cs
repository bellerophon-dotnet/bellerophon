// <copyright file="RuntimeTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System;
    using System.Linq;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using Bellerophon.Runtime;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Xunit;

    /// <summary>
    /// Tests for reading identities and for the tenant membership check.
    /// </summary>
    public class RuntimeTests
    {
        private const string Issuer = "https://example.ciamlogin.com/example/v2.0";

        private static readonly Guid FirmA = Guid.NewGuid();

        private static readonly Guid FirmB = Guid.NewGuid();

        private const string OperatorTool = "7d3c2a51-0000-4000-8000-000000000001";

        /// <summary>
        /// Microsoft's object identifier is the subject when present; Microsoft's per-application sub is ignored.
        /// </summary>
        [Fact]
        public void ObjectIdentifierIsPreferredOverSubject()
        {
            var user = Caller(new Claim("iss", Issuer), new Claim("sub", "per-application"), new Claim("oid", "person"));

            Assert.True(IdentityClaims.TryGetIdentity(user, out var issuer, out var subject));
            Assert.Equal(Issuer, issuer);
            Assert.Equal("person", subject);
        }

        /// <summary>
        /// Without Microsoft's object identifier, the standard subject is used.
        /// </summary>
        [Fact]
        public void StandardSubjectIsUsedOtherwise()
        {
            var user = Caller(new Claim("iss", "https://other.example"), new Claim("sub", "person"));

            Assert.True(IdentityClaims.TryGetIdentity(user, out _, out var subject));
            Assert.Equal("person", subject);
        }

        /// <summary>
        /// Membership is per tenant and ends immediately when removed.
        /// </summary>
        [Fact]
        public void MembershipIndexTracksTenants()
        {
            var index = new MembershipIndex();
            index.Add(FirmA, Issuer, "person");
            index.Add(FirmB, Issuer, "person");

            index.Remove(FirmB, Issuer, "person");

            Assert.True(index.IsMember(FirmA, Issuer, "person"));
            Assert.False(index.IsMember(FirmB, Issuer, "person"));
            Assert.Equal(new[] { FirmA }, index.TenantsOf(Issuer, "person").ToArray());
            Assert.Empty(index.TenantsOf(Issuer, "stranger"));
        }

        /// <summary>
        /// A person acting through an application must belong to the tenant.
        /// </summary>
        [Fact]
        public async Task DelegatedCallerMustBeAMember()
        {
            var index = new MembershipIndex();
            index.Add(FirmA, Issuer, "person");
            var person = Caller(new Claim("scp", "Ledger.Read"), new Claim("iss", Issuer), new Claim("oid", "person"));

            Assert.True(await AuthorizesAsync(index, person, FirmA));
            Assert.False(await AuthorizesAsync(index, person, FirmB));
        }

        /// <summary>
        /// A tool on the allow-list, acting as itself, is authorized by its application permissions alone, in any tenant.
        /// </summary>
        [Fact]
        public async Task AnAllowedToolIsNotCheckedForMembership()
        {
            var tool = Caller(new Claim("idtyp", "app"), new Claim("azp", OperatorTool), new Claim("roles", "Users.Write"));

            Assert.True(await AuthorizesAsync(new MembershipIndex(), tool, FirmB));
        }

        /// <summary>
        /// Version 1.0 tokens name the application in <c>appid</c> rather than <c>azp</c>.
        /// </summary>
        [Fact]
        public async Task AnAllowedToolIsRecognizedInVersion1Tokens()
        {
            var tool = Caller(new Claim("idtyp", "app"), new Claim("appid", OperatorTool), new Claim("roles", "Users.Write"));

            Assert.True(await AuthorizesAsync(new MembershipIndex(), tool, FirmB));
        }

        /// <summary>
        /// An application that is not on the allow-list is not a tool, whatever permissions its token carries.
        /// </summary>
        [Fact]
        public async Task AnApplicationNotOnTheListIsRefused()
        {
            var stranger = Caller(new Claim("idtyp", "app"), new Claim("azp", Guid.NewGuid().ToString()), new Claim("roles", "Ledger.Read"));

            Assert.False(await AuthorizesAsync(new MembershipIndex(), stranger, FirmB));
        }

        /// <summary>
        /// A token without scopes is not taken for a tool's: only a token that says it is an application's can skip membership.
        /// Before, any token without a scope claim skipped it.
        /// </summary>
        [Fact]
        public async Task ATokenWithoutScopesIsNotATool()
        {
            var unscoped = Caller(new Claim("roles", "Users.Write"));
            var listedButUnmarked = Caller(new Claim("azp", OperatorTool), new Claim("roles", "Users.Write"));

            Assert.False(await AuthorizesAsync(new MembershipIndex(), unscoped, FirmB));
            Assert.False(await AuthorizesAsync(new MembershipIndex(), listedButUnmarked, FirmB));
        }

        /// <summary>
        /// A token with delegated scopes acts for a person, so it is checked for membership even if it names a listed application.
        /// </summary>
        [Fact]
        public async Task ADelegatedTokenFromAToolIsCheckedForMembership()
        {
            var person = Caller(new Claim("idtyp", "app"), new Claim("azp", OperatorTool), new Claim("scp", "Ledger.Read"), new Claim("iss", Issuer), new Claim("oid", "person"));

            Assert.False(await AuthorizesAsync(new MembershipIndex(), person, FirmB));
        }

        /// <summary>
        /// The placeholders shipped in configuration are never mistaken for an application's client ID.
        /// </summary>
        [Fact]
        public async Task PlaceholdersMatchNoApplication()
        {
            var options = new ToolAccessOptions { ClientIds = new[] { "<operator-tool-client-id>" } };
            var tool = Caller(new Claim("idtyp", "app"), new Claim("azp", "<operator-tool-client-id>"), new Claim("roles", "Users.Write"));

            Assert.True(ToolAccessOptions.IsPlaceholder("<operator-tool-client-id>"));
            Assert.False(ToolAccessOptions.IsPlaceholder(OperatorTool));
            Assert.False(await AuthorizesAsync(new MembershipIndex(), tool, FirmB, options));
        }

        /// <summary>
        /// The standard OAuth scope claim marks a delegated caller too.
        /// </summary>
        [Fact]
        public void StandardScopeClaimMarksADelegatedCaller()
        {
            var user = Caller(new Claim("scope", "Ledger.Read"));

            Assert.True(ScopeClaims.IsDelegated(user));
            Assert.True(ScopeClaims.HasAny(user, "Ledger.Read"));
        }

        private static async Task<bool> AuthorizesAsync(ITenantMembership membership, ClaimsPrincipal user, Guid tenantId, ToolAccessOptions? tools = null)
        {
            var context = new AuthorizationHandlerContext(new[] { TenantMembershipRequirement.Instance }, user, tenantId);
            var options = Options.Create(tools ?? new ToolAccessOptions { ClientIds = new[] { OperatorTool } });
            await new TenantMembershipHandler(membership, options, NullLogger<TenantMembershipHandler>.Instance).HandleAsync(context);
            return context.HasSucceeded;
        }

        private static ClaimsPrincipal Caller(params Claim[] claims) =>
            new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
    }
}
