// <copyright file="ModelFingerprint.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Computes a model's fingerprint, embedded in both the server and client libraries so each side can tell whether the
    /// other was generated from the same model.
    /// </summary>
    /// <remarks>
    /// The hash covers a compact canonical description of the semantic model, written only for this purpose: every table
    /// (including the built-in ones and the implicit columns), every column, key, reference, unique constraint, and policy,
    /// and the members of every enumeration the model uses.  It omits descriptions, which are documentation, and which tables
    /// feed the rules engine (<c>"facts"</c>), which only the server's working memory sees, so neither forces clients to update.  Comments,
    /// whitespace, property order, and the model writer's layout therefore never change it; only a change in meaning does.
    /// </remarks>
    public static class ModelFingerprint
    {
        /// <summary>
        /// Computes the fingerprint.
        /// </summary>
        /// <param name="model">The model.</param>
        /// <param name="enumMembers">The members of each enumeration the model uses, keyed by CLR type name, when known.</param>
        /// <returns>32 lowercase hexadecimal characters (the first 128 bits of a SHA-256 hash).</returns>
        public static string Compute(DataModel model, IReadOnlyDictionary<string, IReadOnlyList<string>>? enumMembers = null)
        {
            byte[] hash;
            using (var algorithm = SHA256.Create())
            {
                hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(Canonical(model, enumMembers)));
            }

            var builder = new StringBuilder(32);
            for (var index = 0; index < 16; index++)
            {
                builder.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Writes the canonical description that the fingerprint hashes.
        /// </summary>
        /// <param name="model">The model.</param>
        /// <param name="enumMembers">The members of each enumeration, when known.</param>
        /// <returns>One line per element, in canonical order.</returns>
        public static string Canonical(DataModel model, IReadOnlyDictionary<string, IReadOnlyList<string>>? enumMembers = null)
        {
            var lines = new List<string>
            {
                $"model|{model.Name}|{model.Namespace}|{model.Version}",
                $"access|{Access(model.Access)}",
            };

            foreach (var dataType in model.DataTypes)
            {
                lines.Add($"datatype|{dataType.Name}|{dataType.ClrType}|{Number(dataType.MaxLength)}|{Number(dataType.Precision)}|{Number(dataType.Scale)}");
            }

            foreach (var table in model.Tables)
            {
                lines.Add($"table|{table.Name}|{table.Plural}|{string.Join(",", table.Key)}|{table.Persistence}|{table.WriteMode}|{table.Concurrency}|sync={table.IsSynced}|builtIn={table.IsBuiltIn}|{Access(table.Access)}|principal={table.Principal?.Issuer},{table.Principal?.Subject}");
                foreach (var column in table.Columns)
                {
                    lines.Add($"column|{table.Name}.{column.Name}|{column.DataType}|{column.ClrType}|null={column.IsNullable}|{Number(column.MaxLength)}|{Number(column.Precision)}|{Number(column.Scale)}|default={column.DefaultValue}|immutable={column.IsImmutable}|implicit={column.IsImplicit}");
                }

                foreach (var reference in table.References)
                {
                    lines.Add($"reference|{table.Name}.{reference.Name}|{reference.Table}|{string.Join(",", reference.Columns)}|{reference.OnDelete}");
                }

                foreach (var unique in table.Uniques)
                {
                    lines.Add($"unique|{table.Name}.{unique.Name}|{string.Join(",", unique.Columns)}|idempotent={unique.IsIdempotent}");
                }
            }

            if (enumMembers != null)
            {
                foreach (var pair in enumMembers.OrderBy(pair => pair.Key, System.StringComparer.Ordinal))
                {
                    // Enumerations travel by name, so the order of their members does not matter.
                    lines.Add($"enum|{pair.Key}|{string.Join(",", pair.Value.OrderBy(member => member, System.StringComparer.Ordinal))}");
                }
            }

            return string.Join("\n", lines);
        }

        private static string Access(AccessModel? access) =>
            access == null ? "none" : $"{string.Join(",", access.Read)}|{string.Join(",", access.Write)}";

        private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
    }
}
