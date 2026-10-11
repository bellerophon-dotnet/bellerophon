// <copyright file="ModelEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;

    /// <summary>
    /// Emits the root class, named after the model (for example <c>Ledger</c>): the in-memory database, holding one
    /// partition per tenant, and the model's description.
    /// </summary>
    internal static class ModelEmitter
    {
        /// <summary>
        /// Emits the root class.
        /// </summary>
        /// <param name="input">The planned root class.</param>
        /// <returns>The source text.</returns>
        public static string Emit(ModelInput input)
        {
            var quoted = new List<string>();
            foreach (var table in input.Tables)
            {
                quoted.Add($"\"{table}\"");
            }

            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {input.Namespace}");
            writer.Open();
            writer.Summary($"The {input.Name} model, version {input.Version}.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public partial class {Names.Identifier(input.Name)}");
            writer.Open();
            writer.Summary("The model's name.");
            writer.Line($"public const string ModelName = \"{input.Name}\";");
            writer.Line();
            writer.Summary("The contract version: the major number changes for breaking changes, the minor number for additive ones.");
            writer.Line($"public const string Version = \"{input.Version}\";");
            writer.Line();
            writer.Summary("A hash of the model's canonical form. The server and client libraries must carry the same value.");
            writer.Line($"public const string Fingerprint = \"{input.Fingerprint}\";");
            writer.Line();
            writer.Summary("The request header carrying the client's model version.");
            writer.Line("public const string VersionHeader = \"Bellerophon-Model-Version\";");
            writer.Line();
            writer.Summary("The request header carrying the client's model fingerprint.");
            writer.Line("public const string FingerprintHeader = \"Bellerophon-Model-Fingerprint\";");
            writer.Line();
            writer.Summary("The generator role: Primary (server) or Replica (client mirror).");
            writer.Line($"public const string Role = \"{input.Role}\";");
            writer.Line();
            writer.Summary("The version of Bellerophon that generated this code.");
            writer.Line($"public const string GeneratorVersion = \"{Names.Version}\";");
            writer.Line();
            writer.Summary("The tables in this library, in alphabetical order.");
            writer.Line($"public static global::System.Collections.Generic.IReadOnlyList<string> Tables {{ get; }} = new string[] {{ {string.Join(", ", quoted)} }};");
            var ordered = new List<string>();
            foreach (var table in input.TablesInDependencyOrder)
            {
                ordered.Add($"\"{table}\"");
            }

            writer.Line();
            writer.Summary("The tables in dependency order, parents first. Write changed rows in this order and deleted rows in reverse, so foreign keys always hold.");
            writer.Line($"public static global::System.Collections.Generic.IReadOnlyList<string> TablesInDependencyOrder {{ get; }} = new string[] {{ {string.Join(", ", ordered)} }};");
            var partition = $"global::{input.Namespace}.{input.Role}.{input.Name}Partition";
            writer.Line();
            writer.Line($"private readonly global::System.Collections.Concurrent.ConcurrentDictionary<global::System.Guid, {partition}> partitions = new global::System.Collections.Concurrent.ConcurrentDictionary<global::System.Guid, {partition}>();");
            writer.Line();
            writer.Summary("Gets every tenant's partition.");
            writer.Line($"public global::System.Collections.Generic.IEnumerable<{partition}> Partitions => this.partitions.Values;");
            writer.Line();
            writer.Summary("Gets a tenant's partition, creating an empty one the first time.");
            writer.Line("/// <param name=\"tenantId\">The tenant.</param>");
            writer.Line("/// <returns>The partition.</returns>");
            writer.Line($"public {partition} GetPartition(global::System.Guid tenantId)");
            writer.Open();
            writer.Line("if (this.partitions.TryGetValue(tenantId, out var existing))");
            writer.Open();
            writer.Line("return existing;");
            writer.Close();
            writer.Line();
            writer.Line(input.Role == "Primary"
                ? $"var created = new {partition}(tenantId, this.UsesWriteBehind);"
                : $"var created = new {partition}(tenantId);");
            writer.Line("var partition = this.partitions.GetOrAdd(tenantId, created);");
            writer.Line("if (object.ReferenceEquals(partition, created))");
            writer.Open();
            writer.Line("this.PartitionCreated?.Invoke(this, partition);");
            writer.Close();
            writer.Line();
            writer.Line("return partition;");
            writer.Close();
            writer.Line();
            writer.Summary("Occurs once for each new partition, on the thread that created it, so services that run per tenant (a flusher, a rules engine) can start without polling. A handler must be quick and must not throw; list Partitions after subscribing to find those created before.");
            writer.Line($"public event global::System.EventHandler<{partition}>? PartitionCreated;");
            writer.Line();
            writer.Summary("Finds a tenant's partition without creating one.");
            writer.Line("/// <param name=\"tenantId\">The tenant.</param>");
            writer.Line("/// <returns>The partition, or null if the tenant has no rows yet.</returns>");
            writer.Line($"public {partition}? FindPartition(global::System.Guid tenantId)");
            writer.Open();
            writer.Line("return this.partitions.TryGetValue(tenantId, out var partition) ? partition : null;");
            writer.Close();
            writer.Line();
            writer.Summary("Discards a tenant's partition, for example after it faults, so it can be reloaded from the database.");
            writer.Line("/// <param name=\"tenantId\">The tenant.</param>");
            writer.Line("/// <returns>True if the tenant had a partition.</returns>");
            writer.Line("public bool DiscardPartition(global::System.Guid tenantId)");
            writer.Open();
            writer.Line("return this.partitions.TryRemove(tenantId, out _);");
            writer.Close();
            if (input.Role == "Primary")
            {
                writer.Line();
                writer.Summary("Gets a value indicating whether partitions keep committed changes for a write-behind flusher. Without it, every commit counts as durable at once.");
                writer.Line("public bool UsesWriteBehind { get; init; }");
            }

            if (input.Role != "Primary")
            {
                writer.Line();
                writer.Summary("Adds the model version and fingerprint to a request, so the server can refuse a client built from a different model.");
                writer.Line("/// <param name=\"headers\">The request's headers.</param>");
                writer.Line("public static void AddContractHeaders(global::System.Net.Http.Headers.HttpRequestHeaders headers)");
                writer.Open();
                writer.Line("if (headers is null)");
                writer.Open();
                writer.Line("throw new global::System.ArgumentNullException(nameof(headers));");
                writer.Close();
                writer.Line();
                writer.Line("headers.Add(VersionHeader, Version);");
                writer.Line("headers.Add(FingerprintHeader, Fingerprint);");
                writer.Close();
            }

            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
