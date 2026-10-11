// <copyright file="Names.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Text;
    using Microsoft.CodeAnalysis.CSharp;

    /// <summary>
    /// Maps model names and CLR types to C#.
    /// </summary>
    internal static class Names
    {
        /// <summary>
        /// The generator's version, stamped on generated types.
        /// </summary>
        public const string Version = "0.4.0";

        /// <summary>
        /// The C# keywords for common CLR types, so generated code reads naturally.
        /// </summary>
        private static readonly Dictionary<string, string> Keywords = new Dictionary<string, string>
        {
            ["System.Boolean"] = "bool",
            ["System.Byte"] = "byte",
            ["System.Decimal"] = "decimal",
            ["System.Double"] = "double",
            ["System.Int16"] = "short",
            ["System.Int32"] = "int",
            ["System.Int64"] = "long",
            ["System.Object"] = "object",
            ["System.Single"] = "float",
            ["System.String"] = "string",
        };

        /// <summary>
        /// Route constraints for key types that have one.
        /// </summary>
        private static readonly Dictionary<string, string> RouteConstraints = new Dictionary<string, string>
        {
            ["System.Boolean"] = ":bool",
            ["System.Decimal"] = ":decimal",
            ["System.Guid"] = ":guid",
            ["System.Int32"] = ":int",
            ["System.Int64"] = ":long",
        };

        /// <summary>
        /// Escapes a name that is a C# keyword, so a column named <c>class</c> still compiles.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <returns>The identifier.</returns>
        public static string Identifier(string name) =>
            SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

        /// <summary>
        /// Converts a column name to a parameter name, for example <c>AccountId</c> to <c>accountId</c>.
        /// </summary>
        /// <param name="name">The column name.</param>
        /// <returns>The parameter name.</returns>
        public static string Parameter(string name) =>
            Identifier(char.ToLowerInvariant(name[0]) + name.Substring(1));

        /// <summary>
        /// Converts a table name to a route segment, for example <c>OrderIntent</c> to <c>order-intent</c>.
        /// </summary>
        /// <param name="name">The table name.</param>
        /// <returns>The route segment.</returns>
        public static string RouteSegment(string name)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < name.Length; index++)
            {
                if (index > 0 && char.IsUpper(name[index]))
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(name[index]));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Gets the C# type for a CLR type.
        /// </summary>
        /// <param name="clrType">The full CLR type name.</param>
        /// <param name="nullable">Whether the value may be null.</param>
        /// <returns>The C# type.</returns>
        public static string CSharpType(string clrType, bool nullable)
        {
            string type;
            if (clrType.EndsWith("[]", System.StringComparison.Ordinal))
            {
                type = CSharpType(clrType.Substring(0, clrType.Length - 2), false) + "[]";
            }
            else
            {
                type = Keywords.TryGetValue(clrType, out var keyword) ? keyword : "global::" + clrType;
            }

            return nullable ? type + "?" : type;
        }

        /// <summary>
        /// Gets the initializer a non-nullable reference-typed property needs, so generated code has no nullable warnings.
        /// </summary>
        /// <param name="clrType">The full CLR type name.</param>
        /// <param name="kind">The kind of type.</param>
        /// <param name="nullable">Whether the value may be null.</param>
        /// <returns>The initializer, including the leading space, or an empty string.</returns>
        public static string Initializer(string clrType, ClrTypeKind kind, bool nullable)
        {
            if (nullable || kind != ClrTypeKind.Reference)
            {
                return string.Empty;
            }

            if (clrType == "System.String")
            {
                return " = string.Empty;";
            }

            if (clrType.EndsWith("[]", System.StringComparison.Ordinal))
            {
                return $" = global::System.Array.Empty<{CSharpType(clrType.Substring(0, clrType.Length - 2), false)}>();";
            }

            return " = default!;";
        }

        /// <summary>
        /// Gets the route constraint for a key column's type.
        /// </summary>
        /// <param name="clrType">The full CLR type name.</param>
        /// <returns>The constraint, for example <c>:guid</c>, or an empty string.</returns>
        public static string RouteConstraint(string clrType) =>
            RouteConstraints.TryGetValue(clrType, out var constraint) ? constraint : string.Empty;

        /// <summary>
        /// Formats a list of strings as C# string literals.
        /// </summary>
        /// <param name="items">The strings.</param>
        /// <returns>The literals, comma-separated.</returns>
        public static string Literals(IEnumerable<string> items)
        {
            var literals = new List<string>();
            foreach (var item in items)
            {
                literals.Add(SymbolDisplay.FormatLiteral(item, quote: true));
            }

            return string.Join(", ", literals);
        }
    }
}
