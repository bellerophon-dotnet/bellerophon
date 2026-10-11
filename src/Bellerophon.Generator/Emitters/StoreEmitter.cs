// <copyright file="StoreEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits a table's store interface, in wire types: what the controller needs from storage.  The generated store
    /// implements it over the in-memory partitions; a host may substitute its own.
    /// </summary>
    internal static class StoreEmitter
    {
        /// <summary>
        /// Emits a store interface.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <param name="table">The table.</param>
        /// <returns>The source text.</returns>
        public static string Emit(ModelShape shape, TableModel table)
        {
            var dto = shape.DtoType(table);
            var key = shape.KeyParameters(table);
            var task = "global::System.Threading.Tasks.Task";
            var token = "global::System.Threading.CancellationToken cancellationToken";
            var tenant = "global::System.Guid tenantId";

            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary($"Reads and writes {table.Plural} within one tenant.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public partial interface I{table.Name}Store");
            writer.Open();
            writer.Summary("Lists the tenant's rows.");
            writer.Line($"{task}<global::System.Collections.Generic.IReadOnlyList<{dto}>> ListAsync({tenant}, {token});");
            writer.Line();
            writer.Summary("Finds a row by its key; returns null if the tenant has no such row.");
            writer.Line($"{task}<{dto}?> FindAsync({tenant}, {key}, {token});");
            writer.Line();
            writer.Summary("Inserts a row and returns it as stored.");
            writer.Line($"{task}<{dto}> InsertAsync({tenant}, {dto} dto, {token});");
            if (table.WriteMode != WriteMode.AppendOnly)
            {
                writer.Line();
                writer.Summary("Updates a row; returns null if the tenant has no row with that key.");
                writer.Line($"{task}<{dto}?> UpdateAsync({tenant}, {key}, {dto} dto, {token});");
                writer.Line();
                writer.Summary("Deletes the row at the given version; returns false if the tenant has no row with that key.");
                writer.Line($"{task}<bool> DeleteAsync({tenant}, {key}, long rowVersion, {token});");
            }
            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
