// <copyright file="GeneratedFiles.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Linq;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Produces every file for a model in a role.
    /// </summary>
    /// <remarks>
    /// Rows now depend on their neighbors (a parent's children come from other tables), so files are produced from the
    /// whole model.  Each file is compared by its text, so the compiler still reuses every file whose text is unchanged.
    /// </remarks>
    internal static class GeneratedFiles
    {
        /// <summary>
        /// Gets a value indicating whether a model in a role generates database code: the Primary role's SQL Server writer
        /// and loader (when the project references the SQL Server library), or the Schema role's Entity Framework model.
        /// </summary>
        /// <param name="resolved">The resolved model.</param>
        /// <param name="role">The role.</param>
        /// <returns><c>true</c> if column types must map to SQL Server.</returns>
        public static bool GeneratesDatabaseCode(ResolvedModel resolved, RoleSetting role) =>
            role.IsSchema || (role.IsPrimary && resolved.HasSqlServerRuntime);

        /// <summary>
        /// Produces the files.
        /// </summary>
        /// <param name="resolved">The resolved model.</param>
        /// <param name="role">The role.</param>
        /// <returns>The files.</returns>
        public static IEnumerable<GeneratedFile> For(ResolvedModel resolved, RoleSetting role)
        {
            var shape = new ModelShape(resolved, role);
            var name = resolved.Model.Name;
            if (role.IsSchema)
            {
                // The schema role generates only the Entity Framework model, and only when its types all map.
                return resolved.HasEntityFramework && resolved.SqlProblems.Count == 0
                    ? SchemaEmitter.Files(shape).ToList()
                    : new List<GeneratedFile>();
            }

            var prefix = $"{name}.{role.Name}";
            var files = new List<GeneratedFile>
            {
                new GeneratedFile($"{name}.g.cs", ModelEmitter.Emit(Planner.Model(resolved, role))),
                new GeneratedFile($"{prefix}.{name}Partition.g.cs", PartitionEmitter.Emit(shape)),
                new GeneratedFile($"{prefix}.{name}Transaction.g.cs", TransactionEmitter.Emit(shape)),
            };

            if (role.IsPrimary)
            {
                files.Add(new GeneratedFile($"{prefix}.{name}Cut.g.cs", PersistenceEmitter.EmitCut(shape)));
                files.Add(new GeneratedFile($"{prefix}.{name}Changes.g.cs", PersistenceEmitter.EmitChanges(shape)));
                foreach (var table in shape.FactTables)
                {
                    files.Add(new GeneratedFile($"{prefix}.{table.Name}Fact.g.cs", FactEmitter.Emit(shape, table)));
                }

                if (resolved.HasRulesRuntime)
                {
                    files.Add(new GeneratedFile($"{prefix}.{name}RuleActions.g.cs", RulesEmitter.EmitActions(shape)));
                    files.Add(new GeneratedFile($"{prefix}.{name}RulesTenant.g.cs", RulesEmitter.EmitTenant(shape)));
                }
            }

            foreach (var table in shape.Tables)
            {
                files.Add(new GeneratedFile($"{prefix}.{table.Name}.g.cs", RowEmitter.Emit(shape, table)));
                if (ModelShape.IsDurable(table))
                {
                    files.Add(new GeneratedFile($"{prefix}.{table.Name}Dto.g.cs", DtoEmitter.Emit(shape, table)));
                }
            }

            if (!role.IsPrimary)
            {
                return files;
            }

            foreach (var api in Planner.Apis(resolved, role))
            {
                var table = resolved.Model.FindTable(api.Table)!;
                files.Add(new GeneratedFile($"{prefix}.I{api.Table}Store.g.cs", StoreEmitter.Emit(shape, table)));
                files.Add(new GeneratedFile($"{prefix}.{api.Table}Controller.g.cs", ControllerEmitter.Emit(api)));
            }

            var access = Planner.Access(resolved, role);
            if (access.Policies.Count > 0)
            {
                files.Add(new GeneratedFile($"{prefix}.{name}AccessPolicies.g.cs", AccessEmitter.Emit(access)));
            }

            foreach (var server in Planner.Server(resolved, role))
            {
                files.Add(new GeneratedFile($"{prefix}.{name}TenantsController.g.cs", ServerEmitter.EmitTenants(server)));
                files.Add(new GeneratedFile($"{prefix}.{name}ApplicationBuilderExtensions.g.cs", ServerEmitter.EmitContract(server)));
            }

            if (shape.Tables.Any(ModelShape.IsDurable))
            {
                files.Add(new GeneratedFile($"{prefix}.{name}Store.g.cs", StoreImplementationEmitter.Emit(shape)));
                files.Add(new GeneratedFile($"{prefix}.{name}BatchController.g.cs", BatchEmitter.Emit(shape)));
            }

            if (resolved.HasSqlServerRuntime && resolved.SqlProblems.Count == 0 && shape.Tables.Any(ModelShape.IsDurable))
            {
                files.Add(new GeneratedFile($"{prefix}.{name}SqlDatabase.g.cs", SqlServerEmitter.Emit(shape)));
            }

            files.Add(new GeneratedFile($"{prefix}.{name}Membership.g.cs", ServerWiringEmitter.EmitMembership(shape)));
            files.Add(new GeneratedFile($"{prefix}.{name}ServiceCollectionExtensions.g.cs", ServerWiringEmitter.EmitServices(shape)));
            return files;
        }
    }
}
