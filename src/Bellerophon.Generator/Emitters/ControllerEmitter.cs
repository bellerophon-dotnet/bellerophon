// <copyright file="ControllerEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;

    /// <summary>
    /// Emits a table's API controller.  The class requires an authenticated caller; every action requires the table's read
    /// or write policy, which checks the caller's OAuth scopes.  Authorization never depends on data in the ledger.
    /// </summary>
    internal static class ControllerEmitter
    {
        private const string Mvc = "global::Microsoft.AspNetCore.Mvc";

        private const string Authorization = "global::Microsoft.AspNetCore.Authorization";

        private const string Task = "global::System.Threading.Tasks.Task";

        private const string Token = "global::System.Threading.CancellationToken cancellationToken";

        private const string Tenant = "global::System.Guid tenantId";

        /// <summary>
        /// Emits a controller.
        /// </summary>
        /// <param name="input">The planned API.</param>
        /// <returns>The source text.</returns>
        public static string Emit(ApiInput input)
        {
            var dto = $"global::{input.Namespace}.{input.Table}Dto";
            var controller = $"{input.Table}Controller";
            var store = $"I{input.Table}Store";
            var keyParameters = new List<string>();
            var keyArguments = new List<string>();
            var keyRoute = new List<string>();
            var keyMismatch = new List<string>();
            foreach (var part in input.Key)
            {
                keyParameters.Add($"{part.Type} {part.Parameter}");
                keyArguments.Add(part.Parameter);
                keyRoute.Add($"{{{part.Parameter.TrimStart('@')}{part.RouteConstraint}}}");
                keyMismatch.Add($"dto.{part.Column} != {part.Parameter}");
            }

            var route = string.Join("/", keyRoute);
            var parameters = string.Join(", ", keyParameters);
            var arguments = string.Join(", ", keyArguments);
            var read = $"{Authorization}.Authorize(Policy = {input.Model}AccessPolicies.{input.Table}Read)";
            var write = $"{Authorization}.Authorize(Policy = {input.Model}AccessPolicies.{input.Table}Write)";

            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {input.Namespace}.Primary");
            writer.Open();
            writer.Summary($"The API for the {input.Table} table. Reads require {string.Join(" or ", input.Read)}; writes require {string.Join(" or ", input.Write)}.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"[{Mvc}.ApiController]");
            writer.Line($"[{Authorization}.Authorize]");
            writer.Line("[global::Bellerophon.Runtime.MapModelExceptions]");
            writer.Line($"[{Mvc}.Route(\"v{input.MajorVersion}/tenants/{{tenantId:guid}}/{input.Route}\")]");
            writer.Line($"public partial class {controller} : {Mvc}.ControllerBase");
            writer.Open();
            writer.Line($"private readonly {store} store;");
            writer.Line();
            writer.Summary($"Initializes a new instance of the {controller} class.");
            writer.Line($"/// <param name=\"store\">The {input.Table} store.</param>");
            writer.Line($"public {controller}({store} store)");
            writer.Open();
            writer.Line("this.store = store;");
            writer.Close();

            writer.Line();
            writer.Summary("Lists the tenant's rows.");
            writer.Line($"[{Mvc}.HttpGet]");
            writer.Line($"[{read}]");
            writer.Line($"public async {Task}<{Mvc}.ActionResult<global::System.Collections.Generic.IReadOnlyList<{dto}>>> ListAsync({Tenant}, {Token})");
            writer.Open();
            writer.Line("return this.Ok(await this.store.ListAsync(tenantId, cancellationToken).ConfigureAwait(false));");
            writer.Close();

            writer.Line();
            writer.Summary("Gets one row by its key.");
            writer.Line($"[{Mvc}.HttpGet(\"{route}\")]");
            writer.Line($"[{read}]");
            writer.Line($"public async {Task}<{Mvc}.ActionResult<{dto}>> GetAsync({Tenant}, {parameters}, {Token})");
            writer.Open();
            writer.Line($"var row = await this.store.FindAsync(tenantId, {arguments}, cancellationToken).ConfigureAwait(false);");
            writer.Line("if (row is null)");
            writer.Open();
            writer.Line("return this.NotFound();");
            writer.Close();
            writer.Line();
            writer.Line("return this.Ok(row);");
            writer.Close();

            writer.Line();
            writer.Summary("Inserts a row.");
            writer.Line($"[{Mvc}.HttpPost]");
            writer.Line($"[{write}]");
            writer.Line($"public async {Task}<{Mvc}.ActionResult<{dto}>> InsertAsync({Tenant}, [{Mvc}.FromBody] {dto} dto, {Token})");
            writer.Open();
            writer.Line("return this.Ok(await this.store.InsertAsync(tenantId, dto, cancellationToken).ConfigureAwait(false));");
            writer.Close();

            if (!input.IsAppendOnly)
            {
                writer.Line();
                writer.Summary("Updates a row. The key comes from the route; a key the client supplies in the body must match it.");
                writer.Line($"[{Mvc}.HttpPut(\"{route}\")]");
                writer.Line($"[{write}]");
                writer.Line($"public async {Task}<{Mvc}.ActionResult<{dto}>> UpdateAsync({Tenant}, {parameters}, [{Mvc}.FromBody] {dto} dto, {Token})");
                writer.Open();
                if (keyMismatch.Count > 0)
                {
                    writer.Line($"if ({string.Join(" || ", keyMismatch)})");
                    writer.Open();
                    writer.Line("return this.BadRequest();");
                    writer.Close();
                    writer.Line();
                }

                writer.Line($"var updated = await this.store.UpdateAsync(tenantId, {arguments}, dto, cancellationToken).ConfigureAwait(false);");
                writer.Line("if (updated is null)");
                writer.Open();
                writer.Line("return this.NotFound();");
                writer.Close();
                writer.Line();
                writer.Line("return this.Ok(updated);");
                writer.Close();

                writer.Line();
                writer.Summary("Deletes a row by its key. The rowVersion query parameter must be the version being deleted; a stale version is refused with 412.");
                writer.Line($"[{Mvc}.HttpDelete(\"{route}\")]");
                writer.Line($"[{write}]");
                writer.Line($"public async {Task}<{Mvc}.IActionResult> DeleteAsync({Tenant}, {parameters}, [{Mvc}.FromQuery] long rowVersion, {Token})");
                writer.Open();
                writer.Line($"if (await this.store.DeleteAsync(tenantId, {arguments}, rowVersion, cancellationToken).ConfigureAwait(false))");
                writer.Open();
                writer.Line("return this.NoContent();");
                writer.Close();
                writer.Line();
                writer.Line("return this.NotFound();");
                writer.Close();
            }

            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
