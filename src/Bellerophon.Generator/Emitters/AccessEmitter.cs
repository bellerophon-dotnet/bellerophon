// <copyright file="AccessEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    /// <summary>
    /// Emits the access policies: one read and one write policy per table.  Each requires an authenticated caller who
    /// belongs to the tenant in the route (when acting for a person) and whose token carries one of the table's scopes.
    /// </summary>
    internal static class AccessEmitter
    {
        private const string Options = "global::Microsoft.AspNetCore.Authorization.AuthorizationOptions";

        /// <summary>
        /// Emits the access policies.
        /// </summary>
        /// <param name="input">The planned policies.</param>
        /// <returns>The source text.</returns>
        public static string Emit(AccessInput input)
        {
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {input.Namespace}.Primary");
            writer.Open();
            writer.Summary("The access policies generated from the model's access declarations.");
            writer.Line($"/// <remarks>Register them with <c>services.AddAuthorization(options => options.Add{input.Model}AccessPolicies())</c>.</remarks>");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public static partial class {input.Model}AccessPolicies");
            writer.Open();
            foreach (var policy in input.Policies)
            {
                writer.Summary($"Reading {policy.Table} rows requires {string.Join(" or ", policy.Read)}.");
                writer.Line($"public const string {policy.Table}Read = \"{policy.Table}:Read\";");
                writer.Line();
                writer.Summary($"Writing {policy.Table} rows requires {string.Join(" or ", policy.Write)}.");
                writer.Line($"public const string {policy.Table}Write = \"{policy.Table}:Write\";");
                writer.Line();
            }

            writer.Summary("Registers every access policy.");
            writer.Line("/// <param name=\"options\">The authorization options.</param>");
            writer.Line("/// <returns>The same options, for chaining.</returns>");
            writer.Line($"public static {Options} Add{input.Model}AccessPolicies(this {Options} options)");
            writer.Open();
            writer.Line("if (options is null)");
            writer.Open();
            writer.Line("throw new global::System.ArgumentNullException(nameof(options));");
            writer.Close();
            writer.Line();
            foreach (var policy in input.Policies)
            {
                writer.Line($"Add(options, {policy.Table}Read, {Names.Literals(policy.Read)});");
                writer.Line($"Add(options, {policy.Table}Write, {Names.Literals(policy.Write)});");
            }

            writer.Line("return options;");
            writer.Close();
            writer.Line();
            writer.Line($"private static void Add({Options} options, string name, params string[] scopes)");
            writer.Open();
            writer.Line("options.AddPolicy(name, policy => policy");
            writer.Line("    .RequireAuthenticatedUser()");
            writer.Line("    .AddRequirements(global::Bellerophon.Runtime.TenantMembershipRequirement.Instance)");
            writer.Line("    .RequireAssertion(context => global::Bellerophon.Runtime.ScopeClaims.HasAny(context.User, scopes)));");
            writer.Close();
            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
