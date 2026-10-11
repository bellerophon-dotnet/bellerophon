// <copyright file="Fixture.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System;
    using System.IO;

    /// <summary>
    /// Locates test fixtures copied to the output directory.
    /// </summary>
    internal static class Fixture
    {
        /// <summary>
        /// Gets the full path of a fixture.
        /// </summary>
        /// <param name="relativePath">The path under <c>Fixtures</c>.</param>
        /// <returns>The full path.</returns>
        public static string PathOf(string relativePath) => Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath);

        /// <summary>
        /// Reads a fixture's text.
        /// </summary>
        /// <param name="relativePath">The path under <c>Fixtures</c>.</param>
        /// <returns>The text.</returns>
        public static string Read(string relativePath) => File.ReadAllText(PathOf(relativePath));
    }
}
