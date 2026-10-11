// <copyright file="Navigation.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling
{
    using System;
    using System.Collections.Generic;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// The naming rules for the relational model: plurals, and the properties that navigate between parents and children.
    /// </summary>
    /// <remarks>
    /// <para>A child reaches its parent through a property named after the reference: <c>Account.Node</c>, <c>Edge.Parent</c>.</para>
    /// <para>
    /// A parent reaches its children through the plural of the child table (<c>Node.Symbols</c>), or, when each parent has
    /// at most one such child (the reference columns are the child's whole key), through the singular (<c>Node.Account</c>).
    /// The name is decorated with the reference's name whenever that differs from the parent table's name
    /// (<c>Node.EdgesByParent</c>), so adding a relationship never renames an existing property.
    /// </para>
    /// </remarks>
    public static class Navigation
    {
        /// <summary>
        /// Forms the regular English plural of a name, applied to its last word: <c>OrderIntent</c> becomes
        /// <c>OrderIntents</c>.  Irregular plurals are declared in the model with <c>"plural"</c>.
        /// </summary>
        /// <param name="name">A PascalCase name.</param>
        /// <returns>The plural.</returns>
        public static string Plural(string name)
        {
            if (name.Length == 0)
            {
                return name;
            }

            if (EndsWith(name, "s") || EndsWith(name, "x") || EndsWith(name, "z") || EndsWith(name, "ch") || EndsWith(name, "sh"))
            {
                return name + "es";
            }

            if (name.Length > 1 && EndsWith(name, "y") && !IsVowel(name[name.Length - 2]))
            {
                return name.Substring(0, name.Length - 1) + "ies";
            }

            return name + "s";
        }

        /// <summary>
        /// Determines whether each parent has at most one child through a reference: the reference's columns are exactly
        /// the child's key.
        /// </summary>
        /// <param name="child">The child table.</param>
        /// <param name="reference">The reference.</param>
        /// <returns><c>true</c> for a one-to-one extension such as Account of Node.</returns>
        public static bool IsOneToOne(TableModel child, ReferenceModel reference)
        {
            if (reference.Columns.Count != child.Key.Count)
            {
                return false;
            }

            var key = new HashSet<string>(child.Key, StringComparer.Ordinal);
            foreach (var column in reference.Columns)
            {
                if (!key.Contains(column))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Gets the name of the property on the parent row that reaches its children through a reference.
        /// </summary>
        /// <param name="child">The child table.</param>
        /// <param name="reference">The reference.</param>
        /// <returns>For example <c>Symbols</c>, <c>Account</c>, or <c>EdgesByParent</c>.</returns>
        public static string ChildMemberName(TableModel child, ReferenceModel reference)
        {
            var name = IsOneToOne(child, reference) ? child.Name : child.Plural;
            return reference.Name == reference.Table ? name : name + "By" + reference.Name;
        }

        private static bool EndsWith(string name, string suffix) => name.EndsWith(suffix, StringComparison.Ordinal);

        private static bool IsVowel(char character) => "aeiouAEIOU".IndexOf(character) >= 0;
    }
}
