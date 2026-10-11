// <copyright file="Polyfills.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Enables <c>init</c> accessors and records when targeting netstandard2.0.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}
#endif
