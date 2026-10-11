// <copyright file="Planner.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Linq;
    using Bellerophon.Modeling;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Turns a resolved model into the small, equatable inputs each emitter prints.  Every decision about what code
    /// looks like is made here, so the emitters contain no logic beyond formatting.
    /// </summary>
    internal static class Planner
    {
        /// <summary>
        /// Plans the root class.
        /// </summary>
        public static ModelInput Model(ResolvedModel resolved, RoleSetting role)
        {
            var model = resolved.Model;
            var names = new List<string>();
            foreach (var table in resolved.Model.Tables)
            {
                if (role.IsPrimary || (table.IsSynced && table.Persistence == Persistence.Durable))
                {
                    names.Add(table.Name);
                }
            }

            var ordered = new ModelShape(resolved, role).TablesInDependencyOrder().Select(table => table.Name).ToList();
            return new ModelInput(model.Namespace, model.Name, model.Version, ModelFingerprint.Compute(model, resolved.EnumMembers()), role.Name, new EquatableArray<string>(names), new EquatableArray<string>(ordered));
        }

        /// <summary>
        /// Plans the store interface and controller for each durable table (Primary role only).
        /// </summary>
        public static IEnumerable<ApiInput> Apis(ResolvedModel resolved, RoleSetting role)
        {
            var inputs = new List<ApiInput>();
            if (!role.IsPrimary)
            {
                return inputs;
            }

            foreach (var table in resolved.Model.Tables)
            {
                if (table.Persistence != Persistence.Durable)
                {
                    continue;
                }

                var key = new List<KeyPart>();
                foreach (var columnName in table.Key)
                {
                    var column = table.FindColumn(columnName)!;
                    key.Add(new KeyPart(
                        Names.Identifier(column.Name),
                        Names.Parameter(column.Name),
                        Names.CSharpType(column.ClrType, false),
                        Names.RouteConstraint(column.ClrType)));
                }

                inputs.Add(new ApiInput(
                    resolved.Model.Namespace,
                    resolved.Model.Name,
                    resolved.Model.MajorVersion,
                    table.Name,
                    Names.RouteSegment(table.Name),
                    new EquatableArray<KeyPart>(key),
                    table.Access.Read,
                    table.Access.Write,
                    table.WriteMode == WriteMode.AppendOnly));
            }

            return inputs;
        }

        /// <summary>
        /// Plans the access policies (Primary role only; empty otherwise).
        /// </summary>
        public static AccessInput Access(ResolvedModel resolved, RoleSetting role)
        {
            var policies = new List<PolicyPlan>();
            if (role.IsPrimary)
            {
                foreach (var table in resolved.Model.Tables)
                {
                    if (table.Persistence == Persistence.Durable)
                    {
                        policies.Add(new PolicyPlan(table.Name, table.Access.Read, table.Access.Write));
                    }
                }
            }

            return new AccessInput(resolved.Model.Namespace, resolved.Model.Name, new EquatableArray<PolicyPlan>(policies));
        }

        /// <summary>
        /// Plans the server-wide endpoints and wiring (Primary role only).
        /// </summary>
        public static IEnumerable<ServerInput> Server(ResolvedModel resolved, RoleSetting role)
        {
            var model = resolved.Model;
            return role.IsPrimary
                ? new[] { new ServerInput(model.Namespace, model.Name, model.MajorVersion) }
                : System.Array.Empty<ServerInput>();
        }
    }
}
