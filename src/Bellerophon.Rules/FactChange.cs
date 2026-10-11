// <copyright file="FactChange.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Rules
{
    /// <summary>
    /// One change to working memory: a fact to insert or update, or one to retract.
    /// </summary>
    /// <param name="Fact">The fact: immutable, and equal to any other fact for the same row.</param>
    /// <param name="IsRetraction">Whether the fact's row was deleted, so the fact leaves working memory.</param>
    public readonly record struct FactChange(object Fact, bool IsRetraction);
}
