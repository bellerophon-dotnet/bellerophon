// <copyright file="ServerWiringEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Linq;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits the server's wiring: the model's membership (read straight from each tenant's User table) and the single call
    /// that registers the model, its stores, its membership, and its access policies.
    /// </summary>
    internal static class ServerWiringEmitter
    {
        /// <summary>
        /// Emits the membership: the root class answers which tenants an identity belongs to.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <returns>The source text.</returns>
        public static string EmitMembership(ModelShape shape) => EmitMembershipCore(shape);

        /// <summary>
        /// Emits the service registration.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <returns>The source text.</returns>
        public static string EmitServices(ModelShape shape) => EmitServicesCore(shape);

        private static string EmitMembershipCore(ModelShape shape)
        {
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.Namespace}");
            writer.Open();
            writer.Summary("Answers membership straight from each tenant's User table: an identity belongs to a tenant exactly when the tenant has a user bound to it.");
            writer.Line($"public partial class {Names.Identifier(shape.Name)} : global::Bellerophon.Runtime.ITenantMembership");
            writer.Open();
            writer.Summary("Determines whether an identity has a user in a tenant: one lookup in that tenant's identity index.");
            writer.Line("/// <param name=\"tenantId\">The tenant.</param>");
            writer.Line("/// <param name=\"issuer\">The identity's issuer.</param>");
            writer.Line("/// <param name=\"subject\">The identity's subject.</param>");
            writer.Line("/// <returns>True if the identity is a member of the tenant.</returns>");
            writer.Line("public bool IsMember(global::System.Guid tenantId, string issuer, string subject)");
            writer.Open();
            writer.Line("return this.FindPartition(tenantId)?.FindUserByIdentity(issuer, subject) != null;");
            writer.Close();
            writer.Line();
            writer.Summary("Lists the tenants an identity belongs to, for the tenant picker.");
            writer.Line("/// <param name=\"issuer\">The identity's issuer.</param>");
            writer.Line("/// <param name=\"subject\">The identity's subject.</param>");
            writer.Line("/// <returns>The tenants.</returns>");
            writer.Line("public global::System.Collections.Generic.IReadOnlyList<global::System.Guid> TenantsOf(string issuer, string subject)");
            writer.Open();
            writer.Line("var tenants = new global::System.Collections.Generic.List<global::System.Guid>();");
            writer.Line("foreach (var partition in this.Partitions)");
            writer.Open();
            writer.Line("if (partition.FindUserByIdentity(issuer, subject) != null)");
            writer.Open();
            writer.Line("tenants.Add(partition.TenantId);");
            writer.Close();
            writer.Close();
            writer.Line();
            writer.Line("return tenants;");
            writer.Close();
            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        private static string EmitServicesCore(ModelShape shape)
        {
            var services = "global::Microsoft.Extensions.DependencyInjection.IServiceCollection";
            var extensions = "global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions";
            var provider = "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions";
            var store = $"global::{shape.RoleNamespace}.{shape.Name}Store";
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary($"Registers the {shape.Name} model with a server.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public static partial class {shape.Name}ServiceCollectionExtensions");
            writer.Open();
            writer.Summary("Registers the in-memory model, its stores, its tenant membership, and its access policies, with the runtime's membership check.");
            writer.Line("/// <param name=\"services\">The services.</param>");
            writer.Line("/// <returns>The same services, for chaining.</returns>");
            writer.Line($"public static {services} Add{shape.Name}(this {services} services)");
            writer.Open();
            writer.Line("// A host that registers its own model first (for example, one that uses write-behind) keeps it.");
            writer.Line($"global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<{shape.RootType}>(services);");
            writer.Line($"{extensions}.AddSingleton<global::Bellerophon.Runtime.ITenantMembership>(services, serviceProvider => {provider}.GetRequiredService<{shape.RootType}>(serviceProvider));");
            writer.Line($"{extensions}.AddSingleton<{store}>(services);");
            foreach (var table in shape.Tables.Where(table => table.Persistence == Persistence.Durable))
            {
                writer.Line($"{extensions}.AddSingleton<I{table.Name}Store>(services, serviceProvider => {provider}.GetRequiredService<{store}>(serviceProvider));");
            }

            writer.Line($"global::Microsoft.Extensions.DependencyInjection.PolicyServiceCollectionExtensions.AddAuthorization(services, options => options.Add{shape.Name}AccessPolicies());");
            writer.Line("global::Bellerophon.Runtime.BellerophonServiceCollectionExtensions.AddBellerophonAuthorization(services);");
            writer.Line("return services;");
            writer.Close();
            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
