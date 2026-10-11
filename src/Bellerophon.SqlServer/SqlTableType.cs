// <copyright file="SqlTableType.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.SqlServer
{
    /// <summary>
    /// A user-defined table type the generated writer passes rows in.
    /// </summary>
    /// <param name="Name">The type's quoted, schema-qualified name, for example <c>[bellerophon].[NodeRows_1a2b3c4d]</c>.</param>
    /// <param name="Definition">The statement that creates it.</param>
    public sealed record SqlTableType(string Name, string Definition);
}
