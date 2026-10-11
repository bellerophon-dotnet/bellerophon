// <copyright file="ServerEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    /// <summary>
    /// Emits the Primary role's server-wide pieces: the tenant picker endpoint and the contract check's registration.
    /// </summary>
    internal static class ServerEmitter
    {
        /// <summary>
        /// Emits the tenant picker: <c>GET v{major}/me/tenants</c>, listing the tenants the caller belongs to.
        /// </summary>
        /// <param name="input">The planned server pieces.</param>
        /// <returns>The source text.</returns>
        public static string EmitTenants(ServerInput input)
        {
            var controller = $"{input.Model}TenantsController";
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {input.Namespace}.Primary");
            writer.Open();
            writer.Summary("Lists the tenants the caller belongs to, for the tenant picker. Requires only an authenticated caller.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line("[global::Microsoft.AspNetCore.Mvc.ApiController]");
            writer.Line("[global::Microsoft.AspNetCore.Authorization.Authorize]");
            writer.Line($"[global::Microsoft.AspNetCore.Mvc.Route(\"v{input.MajorVersion}/me/tenants\")]");
            writer.Line($"public partial class {controller} : global::Microsoft.AspNetCore.Mvc.ControllerBase");
            writer.Open();
            writer.Line("private readonly global::Bellerophon.Runtime.ITenantMembership membership;");
            writer.Line();
            writer.Summary($"Initializes a new instance of the {controller} class.");
            writer.Line("/// <param name=\"membership\">The membership to read.</param>");
            writer.Line($"public {controller}(global::Bellerophon.Runtime.ITenantMembership membership)");
            writer.Open();
            writer.Line("this.membership = membership;");
            writer.Close();
            writer.Line();
            writer.Summary("Lists the caller's tenants. A tool acting as itself belongs to no tenant and receives an empty list.");
            writer.Line("[global::Microsoft.AspNetCore.Mvc.HttpGet]");
            writer.Line("public global::Microsoft.AspNetCore.Mvc.ActionResult<global::System.Collections.Generic.IReadOnlyList<global::System.Guid>> List()");
            writer.Open();
            writer.Line("if (global::Bellerophon.Runtime.IdentityClaims.TryGetIdentity(this.User, out var issuer, out var subject))");
            writer.Open();
            writer.Line("return this.Ok(this.membership.TenantsOf(issuer, subject));");
            writer.Close();
            writer.Line();
            writer.Line("return this.Ok(global::System.Array.Empty<global::System.Guid>());");
            writer.Close();
            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        /// <summary>
        /// Emits the contract check's registration, bound to the model's version and fingerprint.
        /// </summary>
        /// <param name="input">The planned server pieces.</param>
        /// <returns>The source text.</returns>
        public static string EmitContract(ServerInput input)
        {
            var builder = "global::Microsoft.AspNetCore.Builder.IApplicationBuilder";
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {input.Namespace}.Primary");
            writer.Open();
            writer.Summary($"Registers the check that refuses clients generated from a different {input.Model} model.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public static partial class {input.Model}ApplicationBuilderExtensions");
            writer.Open();
            writer.Summary("Adds the contract check to the request pipeline: a client whose model version or fingerprint differs receives 426 Upgrade Required.");
            writer.Line("/// <param name=\"application\">The application.</param>");
            writer.Line("/// <returns>The same application, for chaining.</returns>");
            writer.Line($"public static {builder} Use{input.Model}Contract(this {builder} application)");
            writer.Open();
            writer.Line($"return global::Bellerophon.Runtime.ModelContractApplicationBuilderExtensions.UseModelContract(application, global::{input.Namespace}.{Names.Identifier(input.Model)}.Version, global::{input.Namespace}.{Names.Identifier(input.Model)}.Fingerprint);");
            writer.Close();
            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
